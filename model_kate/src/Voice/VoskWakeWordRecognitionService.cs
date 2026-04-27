using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Vosk;

namespace model_kate.Voice
{
    public class VoskWakeWordRecognitionService : IVoiceRecognitionService, IDisposable
    {
        private const double CommandStartTimeoutSeconds = 8.0;
        private const double CommandSilenceTimeoutSeconds = 1.5;
        private const float MicrophoneGain = 5.0f; // aumentado de 3.5 para 5.0

        /// <summary>
        /// Frases que indicam que o usuário está se dirigindo à IA sem usar a wake word.
        /// Ativas somente quando há uma conversa recente (dentro de ContextualActivationWindow).
        /// </summary>
        private static readonly string[] ContextualActivationPhrases =
        {
            "e ai", "e aí", "voce sabe", "voce pode", "voce consegue", "voce acha",
            "voce tem", "me diz", "me diga", "me explica", "me explique", "me fala",
            "o que voce", "como voce", "sabe me dizer", "tem ideia", "pode me dizer",
            "pode me ajudar", "pode explicar", "pode me explicar", "voce conhece",
            "voce entende", "sabe disso", "me conta", "me conte"
        };

        private static readonly TimeSpan ContextualActivationWindow = TimeSpan.FromMinutes(3);
        // Modo Dialogo: apos Kate responder, qualquer fala vira comando por este tempo
        private static readonly TimeSpan DialogueModeWindow = TimeSpan.FromSeconds(45);
        private const int DialogueModeMinChars = 6; // evita ruidos curtos
        private DateTime _lastSuccessfulCommandUtc = DateTime.MinValue;
        private DateTime _dialogueModeUntilUtc = DateTime.MinValue;
        private DateTime _commandEnergyIgnoreUntilUtc = DateTime.MinValue;
        private static readonly string[] DefaultWakeWords = { "kate", "keiti", "kayte" };
        private static readonly (string Pattern, string Replacement)[] CommandCorrections =
        {
            (@"\bque ora\b", "que hora"),
            (@"\bqual e a ora\b", "qual e a hora"),
            (@"\bqual é a ora\b", "qual é a hora"),
            (@"\bora e\b", "hora e"),
            (@"\bora é\b", "hora é"),
            // Correções comuns de STT para comandos em inglês e nomes de app.
            (@"\bspotfy\b", "spotify"),
            (@"\bspotifi\b", "spotify"),
            (@"\bspotfyy\b", "spotify"),
            (@"\bspotyfy\b", "spotify"),
            (@"\bspoti\b", "spotify")
        };

        public event Action<string>? OnTextRecognized;
    private readonly string[] _wakeWords;
        private readonly string[] _wakeWordCanonicalForms;
    private readonly string _wakeWordCleanupPattern;
        private readonly Model _wakeModel;
        private readonly VoskRecognizer _wakeRecognizer;
        private readonly string? _commandModelPath;
        private readonly LocalCommandTranscriptionService? _commandTranscriber;
        private Model? _commandModel;
        private VoskRecognizer? _commandRecognizer;
        private readonly WaveInEvent _waveIn;
        private VoskRecognizer? _recognizer;
        private bool _listeningForCommand = false;
        private bool _hasDetectedCommandSpeech = false;
        private DateTime _commandListeningStartedUtc = DateTime.MinValue;
        private DateTime _lastVoiceTimeUtc = DateTime.MinValue;
        private DateTime _ignoreAudioUntil = DateTime.MinValue;
        private bool _externallySuppressed = false;
        private string _partialCommand = "";
        private string _latestPartial = "";
        private DateTime _lastWakeCandidateLogUtc = DateTime.MinValue;
        private readonly MemoryStream _commandAudioBuffer = new();
        private readonly object _commandAudioSync = new();
        private CancellationTokenSource? _cts = null;
        // Reset diferido: Reset() só pode ser chamado na thread de gravação do NAudio.
        // SwitchTo*Recognizer() apenas seta a flag; OnDataAvailable aplica o reset.
        private volatile bool _pendingReset = false;
        private readonly SpeakerProfileService _speaker;
        private bool _enrollmentPending = false;
        private readonly System.Collections.Generic.List<byte> _enrollBuffer = new();

        public event Action<string>? OnWakeWordDetected;
        public event Action<string>? OnCommandDetected;
        public event Action? OnCommandCanceled;
        public VoskWakeWordRecognitionService(string wakeWordModelPath, string? commandModelPath = null)
        {
            Vosk.Vosk.SetLogLevel(0);
            _wakeWords = ResolveWakeWords();
            _wakeWordCanonicalForms = _wakeWords
                .Select(ToWakeWordCanonicalForm)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            _wakeWordCleanupPattern = BuildWakeWordCleanupPattern(_wakeWords);
            _wakeModel = new Model(wakeWordModelPath);
            _wakeRecognizer = CreateRecognizer(_wakeModel);
            _commandModelPath = NormalizePathOrNull(commandModelPath, wakeWordModelPath);
            _commandTranscriber = LocalCommandTranscriptionService.Create(AppContext.BaseDirectory);
            AppendVoiceLog($"[Voice] Wake words ativas: {string.Join(", ", _wakeWords)}");
            if (_commandTranscriber is not null)
            {
                AppendVoiceLog($"[Voice] Transcritor local habilitado: {_commandTranscriber.Description}");
            }
            _recognizer = _wakeRecognizer;
            _speaker = new SpeakerProfileService();
            if (_speaker.IsEnrolled)
                AppendVoiceLog("[Voice] Perfil de voz do usuário carregado.");
            else
                AppendVoiceLog("[Voice] Nenhum perfil de voz registrado. Diga 'Kate, aprenda minha voz' para registrar.");
            _waveIn = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(16000, 1),
                BufferMilliseconds = 100
            };
            _waveIn.DataAvailable += OnDataAvailable;
        }

        public void Start()
        {
            _listeningForCommand = false;
            SwitchToWakeRecognizer();
            _cts = new CancellationTokenSource();
            _waveIn.StartRecording();
        }

        public void Stop()
        {
            _cts?.Cancel();
            _waveIn.StopRecording();
        }

        public void SuspendCapture(TimeSpan duration)
        {
            _ignoreAudioUntil = DateTime.UtcNow.Add(duration);
            ResetCommandState();
            _lastVoiceTimeUtc = _ignoreAudioUntil;
            SwitchToWakeRecognizer();
        }

        public void BeginCaptureSuppression()
        {
            _externallySuppressed = true;
            _ignoreAudioUntil = DateTime.MaxValue;
            ResetCommandState();
            SwitchToWakeRecognizer();
        }

        public void EndCaptureSuppression(TimeSpan cooldown)
        {
            _externallySuppressed = false;
            SuspendCapture(cooldown);
        }

        public void BeginCommandListening()
        {
            SwitchToCommandRecognizer();
            var nowUtc = DateTime.UtcNow;
            _listeningForCommand = true;
            _hasDetectedCommandSpeech = false;
            _commandListeningStartedUtc = nowUtc;
            _lastVoiceTimeUtc = nowUtc;
            _partialCommand = string.Empty;
            _latestPartial = string.Empty;
            // Ignora energia de voz nos primeiros 400ms para evitar que o eco da wake word
            // (ainda no buffer do microfone) seja contado como início de comando.
            _commandEnergyIgnoreUntilUtc = nowUtc.AddMilliseconds(400);
            ClearCommandAudioBuffer();
            // reset será aplicado na próxima chamada de OnDataAvailable (thread de gravação)
        }

        public async Task<string> RecognizeOnceAsync()
        {
            // Não implementado para fluxo Alexa-like contínuo
            throw new NotImplementedException();
        }

        /// <summary>Inicia o modo de enrollment: próximos ~4s de áudio serão usados para registrar o perfil de voz.</summary>
        public void StartVoiceEnrollment()
        {
            _enrollBuffer.Clear();
            _enrollmentPending = true;
            AppendVoiceLog("[Voice] Enrollment iniciado. Fale normalmente por 4 segundos...");
        }

        /// <summary>Ativa/desativa a verificação de locutor.</summary>
        public bool SpeakerVerificationEnabled
        {
            get => _speaker.VerificationEnabled;
            set { _speaker.VerificationEnabled = value; AppendVoiceLog($"[Voice] Verificação de locutor: {(value ? "ativada" : "desativada")}"); }
        }

        /// <summary>
        /// Amplifica amostras PCM 16-bit com clipping para evitar overflow.
        /// </summary>
        /// <summary>
        /// Retorna true se o buffer PCM 16-bit contém energia de voz acima do limiar.
        /// Usado para detectar fala em inglês que o Vosk (modelo pt) não reconhece.
        /// </summary>
        private static bool HasVoiceEnergy(byte[] buffer, int bytesRecorded, short peakThreshold = 280, float averageThreshold = 0.013f)
        {
            long sumAbs = 0;
            var sampleCount = 0;
            for (var i = 0; i < bytesRecorded - 1; i += 2)
            {
                var sample = Math.Abs((short)(buffer[i] | (buffer[i + 1] << 8)));
                sumAbs += sample;
                sampleCount++;
                if (sample >= peakThreshold)
                    return true;
            }

            if (sampleCount == 0)
                return false;

            // RMS simplificado por média absoluta normalizada em PCM16.
            var avg = (float)sumAbs / sampleCount;
            var normalizedAvg = avg / short.MaxValue;
            return normalizedAvg >= averageThreshold;
        }

        private static byte[] AmplifyBuffer(byte[] buffer, int bytesRecorded, float gain)
        {
            var amplified = new byte[bytesRecorded];
            for (var i = 0; i < bytesRecorded - 1; i += 2)
            {
                var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                var boosted = (int)(sample * gain);
                if (boosted > short.MaxValue) boosted = short.MaxValue;
                else if (boosted < short.MinValue) boosted = short.MinValue;
                amplified[i] = (byte)(boosted & 0xFF);
                amplified[i + 1] = (byte)((boosted >> 8) & 0xFF);
            }
            return amplified;
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            var nowUtc = DateTime.UtcNow;
            var isSuppressed = _externallySuppressed || nowUtc < _ignoreAudioUntil;

            var audioBuffer = MicrophoneGain > 1.0f
                ? AmplifyBuffer(e.Buffer, e.BytesRecorded, MicrophoneGain)
                : e.Buffer;
            var audioBytes = e.BytesRecorded;

            // ── Enrollment de voz ──────────────────────────────────────────
            if (_enrollmentPending)
            {
                _enrollBuffer.AddRange(new ArraySegment<byte>(audioBuffer, 0, audioBytes));
                // 4 segundos a 16kHz PCM16 = 16000 * 2 * 4 = 128000 bytes
                if (_enrollBuffer.Count >= 128_000)
                {
                    _enrollmentPending = false;
                    var buf = _enrollBuffer.ToArray();
                    _enrollBuffer.Clear();
                    bool ok = _speaker.EnrollFromPcm(buf, buf.Length);
                    AppendVoiceLog(ok
                        ? "[Voice] Perfil de voz registrado com sucesso!"
                        : "[Voice] Enrollment falhou: audio insuficiente.");
                    OnCommandDetected?.Invoke(ok
                        ? "Pronto! Aprendi sua voz. Agora vou tentar focar apenas em você."
                        : "Não consegui registrar sua voz. Tente novamente em ambiente mais silencioso.");
                }
                return;
            }

            // ── Verificação de locutor ─────────────────────────────────────
            // Rejeita áudio de outros locutores se perfil estiver registrado.
            // Durante a captura de comando, não bloqueia por speaker verification para evitar
            // falso negativo logo após a wake word (sintoma: fica em "Aguardando comando...").
            if (_speaker.IsEnrolled && !isSuppressed && !_listeningForCommand && audioBytes >= 3200)
            {
                if (!_speaker.Verify(audioBuffer, audioBytes))
                {
                    return; // voz de outro locutor — ignora
                }
            }

            if (_listeningForCommand && !_externallySuppressed)
            {
                AppendCommandAudio(audioBuffer, audioBytes);

                // Detecta presença de voz por energia RMS do PCM, independente do Vosk.
                // Necessário para inglês: Vosk (modelo pt) não produz texto para inglês,
                // mas o áudio existe e deve ser repassado ao Whisper.
                // Ignora energia nos primeiros 400ms para não capturar eco da wake word.
                var energyAllowed = nowUtc >= _commandEnergyIgnoreUntilUtc;
                if (energyAllowed)
                {
                    if (!_hasDetectedCommandSpeech && HasVoiceEnergy(audioBuffer, audioBytes))
                    {
                        _hasDetectedCommandSpeech = true;
                        _lastVoiceTimeUtc = nowUtc;
                    }
                    else if (_hasDetectedCommandSpeech && HasVoiceEnergy(audioBuffer, audioBytes))
                    {
                        _lastVoiceTimeUtc = nowUtc;
                    }
                }
            }

            if (isSuppressed)
            {
                return;
            }

            if (_recognizer is null)
            {
                return;
            }

            // Aplica reset pendente — garantido estar na thread de gravação do NAudio
            if (_pendingReset)
            {
                _pendingReset = false;
                _recognizer.Reset();
            }

            bool accepted;
            string voskOutput;
            accepted = _recognizer.AcceptWaveform(audioBuffer, audioBytes);
            voskOutput = accepted ? _recognizer.Result() : _recognizer.PartialResult();

            if (accepted)
            {
                var text = ExtractText(voskOutput);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    OnTextRecognized?.Invoke(text);
                    if (!_listeningForCommand)
                    {
                        if (TryDetectWakeWord(text, isPartial: false))
                        {
                            return;
                        }

                        MaybeLogWakeWordCandidate(text, isPartial: false);
                    }
                    else
                    {
                        _partialCommand = AppendSegment(_partialCommand, text);
                        _hasDetectedCommandSpeech = true;
                        _latestPartial = string.Empty;
                        _lastVoiceTimeUtc = nowUtc;
                    }
                }
            }
            else
            {
                var partial = ExtractPartial(voskOutput);
                if (!string.IsNullOrWhiteSpace(partial))
                {
                    OnTextRecognized?.Invoke(partial);
                    if (!_listeningForCommand)
                    {
                        if (TryDetectWakeWord(partial, isPartial: true))
                        {
                            return;
                        }

                        MaybeLogWakeWordCandidate(partial, isPartial: true);
                    }
                }
                if (_listeningForCommand && !string.IsNullOrWhiteSpace(partial))
                {
                    _hasDetectedCommandSpeech = true;
                    _latestPartial = partial.Trim();
                    _lastVoiceTimeUtc = nowUtc;
                }
            }

            if (!_listeningForCommand)
            {
                return;
            }

            if (!_hasDetectedCommandSpeech && (nowUtc - _commandListeningStartedUtc).TotalSeconds >= CommandStartTimeoutSeconds)
            {
                var bufferedAudio = TakeCommandAudioSnapshot();
                if (bufferedAudio.Length >= 12_000)
                {
                    AppendVoiceLog("[Voice] Timeout inicial sem texto Vosk. Tentando transcricao por Whisper com audio bruto.");
                    EmitCommand(string.Empty, bufferedAudio);
                    return;
                }

                AppendVoiceLog("[Voice] Escuta cancelada por timeout inicial (sem deteccao de fala). ");
                CancelCommandListening();
                return;
            }

            if (_hasDetectedCommandSpeech && (nowUtc - _lastVoiceTimeUtc).TotalSeconds >= CommandSilenceTimeoutSeconds)
            {
                EmitCommand(BuildPendingCommand());
            }
        }

        private string BuildPendingCommand()
        {
            var combinedCommand = _partialCommand.Trim();

            if (_recognizer is null)
            {
                return string.IsNullOrWhiteSpace(combinedCommand) ? _latestPartial.Trim() : combinedCommand;
            }

            try
            {
                string finalResult;
                finalResult = ExtractText(_recognizer.FinalResult());
                if (!string.IsNullOrWhiteSpace(finalResult))
                {
                    combinedCommand = string.IsNullOrWhiteSpace(combinedCommand)
                        ? finalResult.Trim()
                        : AppendSegment(combinedCommand, finalResult);
                }
            }
            catch
            {
            }

            var candidate = string.IsNullOrWhiteSpace(combinedCommand) ? _latestPartial.Trim() : combinedCommand;
            return CleanCommandCandidate(candidate);
        }

        private void EmitCommand(string commandText, byte[]? capturedAudio = null)
        {
            var command = commandText.Trim();
            var commandAudio = capturedAudio ?? TakeCommandAudioSnapshot();
            ResetCommandState();
            SwitchToWakeRecognizer();
            if (!string.IsNullOrWhiteSpace(command))
            {
                _ = Task.Run(async () =>
                {
                    var finalCommand = await ResolveCommandTextAsync(command, commandAudio).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(finalCommand))
                    {
                        AppendVoiceLog("[Voice] Comando descartado: apenas eco da wake word");
                        OnCommandCanceled?.Invoke();
                        return;
                    }

                    _lastSuccessfulCommandUtc = DateTime.UtcNow;
                    AppendVoiceLog($"[Voice] Comando emitido: {finalCommand}");
                    OnCommandDetected?.Invoke(finalCommand);
                });
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    // Fallback: mesmo sem texto do Vosk, tenta transcrever o áudio bruto no Whisper.
                    var whisperOnlyCommand = await ResolveCommandTextAsync(string.Empty, commandAudio).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(whisperOnlyCommand))
                    {
                        _lastSuccessfulCommandUtc = DateTime.UtcNow;
                        AppendVoiceLog($"[Voice] Comando emitido via fallback Whisper: {whisperOnlyCommand}");
                        OnCommandDetected?.Invoke(whisperOnlyCommand);
                        return;
                    }

                    // Nenhum texto capturado após a wake word — reseta UI para aguardar próxima ativação
                    AppendVoiceLog("[Voice] Comando vazio: nenhum texto detectado apos wake word");
                    OnCommandCanceled?.Invoke();
                });
            }
            // reset pendente já setado por SwitchToWakeRecognizer()
        }

        private void CancelCommandListening()
        {
            AppendVoiceLog("[Voice] Escuta cancelada: nenhum comando apos wake word");
            ResetCommandState();
            SwitchToWakeRecognizer();
            OnCommandCanceled?.Invoke();
            // reset pendente já setado por SwitchToWakeRecognizer()
        }

        private VoskRecognizer CreateRecognizer(Model model)
        {
            var recognizer = new VoskRecognizer(model, 16000.0f);
            recognizer.SetWords(true);
            recognizer.SetPartialWords(true);
            return recognizer;
        }

        private void SwitchToWakeRecognizer()
        {
            _recognizer = _wakeRecognizer;
            _pendingReset = true; // aplicado na thread de gravação
        }

        private void SwitchToCommandRecognizer()
        {
            if (EnsureCommandRecognizer())
                _recognizer = _commandRecognizer;
            else
                _recognizer = _wakeRecognizer;
            _pendingReset = true; // aplicado na thread de gravação
        }

        private bool EnsureCommandRecognizer()
        {
            if (_commandRecognizer is not null)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(_commandModelPath))
            {
                return false;
            }

            try
            {
                AppendVoiceLog($"[Voice] Carregando modelo de comando: {_commandModelPath}");
                _commandModel = new Model(_commandModelPath);
                _commandRecognizer = CreateRecognizer(_commandModel);
                AppendVoiceLog($"[Voice] Modelo de comando pronto: {_commandModelPath}");
                return true;
            }
            catch (Exception ex)
            {
                AppendVoiceLog($"[Voice] Falha ao carregar modelo de comando. Mantendo modelo de wake word. {ex.Message}");
                try { _commandRecognizer?.Dispose(); } catch { }
                try { _commandModel?.Dispose(); } catch { }
                _commandRecognizer = null;
                _commandModel = null;
                return false;
            }
        }

        private static string? NormalizePathOrNull(string? commandModelPath, string wakeWordModelPath)
        {
            if (string.IsNullOrWhiteSpace(commandModelPath))
            {
                return null;
            }

            return string.Equals(
                System.IO.Path.GetFullPath(commandModelPath),
                System.IO.Path.GetFullPath(wakeWordModelPath),
                StringComparison.OrdinalIgnoreCase)
                ? null
                : commandModelPath;
        }

        internal static void AppendVoiceLog(string message)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "erro_kate.txt"), message + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static string AppendSegment(string currentText, string newText)
        {
            var current = currentText.Trim();
            var segment = newText.Trim();
            if (string.IsNullOrWhiteSpace(segment))
            {
                return current;
            }

            if (string.IsNullOrWhiteSpace(current))
            {
                return segment;
            }

            if (string.Equals(current, segment, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            var currentWords = current.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var segmentWords = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var maxOverlap = Math.Min(currentWords.Length, segmentWords.Length);

            for (var overlap = maxOverlap; overlap >= 1; overlap--)
            {
                var suffix = currentWords[^overlap..];
                var prefix = segmentWords[..overlap];
                if (suffix.SequenceEqual(prefix, StringComparerFromNormalize.Instance))
                {
                    var merged = currentWords.Concat(segmentWords[overlap..]);
                    return CollapseRepeatedWords(string.Join(" ", merged));
                }
            }

            return CollapseRepeatedWords((current + " " + segment).Trim());
        }

        private static string PostProcessCommand(string command, string wakeWordCleanupPattern)
        {
            var cleaned = CollapseRepeatedWords(command);
            cleaned = Regex.Replace(cleaned, wakeWordCleanupPattern, string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();

            foreach (var (pattern, replacement) in CommandCorrections)
            {
                cleaned = Regex.Replace(cleaned, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            cleaned = Regex.Replace(cleaned, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
            return cleaned;
        }

        private string CleanCommandCandidate(string command)
        {
            var cleaned = PostProcessCommand(command, _wakeWordCleanupPattern);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return string.Empty;
            }

            return IsWakeWordEcho(cleaned) ? string.Empty : cleaned;
        }

        private bool IsWakeWordEcho(string text)
        {
            var normalized = NormalizeText(text);
            var canonical = ToWakeWordCanonicalForm(text);
            return _wakeWords.Contains(normalized, StringComparer.Ordinal)
                || _wakeWordCanonicalForms.Contains(canonical, StringComparer.Ordinal);
        }

        private static string CollapseRepeatedWords(string text)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= 1)
            {
                return text.Trim();
            }

            var collapsed = new List<string>(words.Length);
            foreach (var word in words)
            {
                if (collapsed.Count > 0 && StringComparerFromNormalize.Instance.Equals(collapsed[^1], word))
                {
                    continue;
                }

                collapsed.Add(word);
            }

            return string.Join(" ", collapsed);
        }

        private bool ContainsWakeWord(string text)
        {
            var normalizedText = NormalizeText(text);
            if (_wakeWords.Any(wakeWord => normalizedText.Contains(wakeWord, StringComparison.Ordinal)))
            {
                return true;
            }

            // Guarda contra falso positivo de "quente":
            // "quente" sozinho (ou "quente quente") pode ser Kate.
            // Mas "esta quente", "muito quente hoje", etc. e fala normal.
            if (normalizedText.Contains("quente", StringComparison.Ordinal)
                && IsQuenteInTemperatureContext(normalizedText))
            {
                return false;
            }

            var canonicalText = ToWakeWordCanonicalForm(text);
            return _wakeWordCanonicalForms.Any(canonicalWakeWord => canonicalText.Contains(canonicalWakeWord, StringComparison.Ordinal));
        }

        // Palavras que, quando aparecem junto com "quente", indicam contexto de temperatura
        // e NAO de wake word.
        private static readonly string[] QuenteContextWords =
        {
            "esta", "estou", "ta", "muito", "pouco", "faz", "dia", "hoje", "calor",
            "aqui", "bem", "demais", "menos", "mais", "frio", "clima", "tempo",
            "tao", "temperatura", "agua", "comida", "cafe", "sol", "verao", "inverno",
            "quarto", "casa", "rua", "la", "aqueceu", "esquentar", "quentinho",
            "ficou", "fico", "vai", "tava", "foi", "certeza", "demais", "igual"
        };

        private static bool IsQuenteInTemperatureContext(string normalizedText)
        {
            var words = normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Se so tem "quente" (possivelmente repetido), e provavel wake word
            if (words.All(w => w.Equals("quente", StringComparison.Ordinal)))
            {
                return false; // nao e contexto de temperatura, pode ser Kate
            }
            // Se ha palavras de contexto de temperatura, e fala normal
            return QuenteContextWords.Any(ctx => normalizedText.Contains(ctx, StringComparison.Ordinal));
        }

        /// <summary>Ativa o modo diálogo: próximas falas são aceitas sem wake word.</summary>
        public void EnterDialogueMode(TimeSpan? duration = null)
        {
            _dialogueModeUntilUtc = DateTime.UtcNow.Add(duration ?? DialogueModeWindow);
            AppendVoiceLog($"[Voice] Modo dialogo ativado por {(duration ?? DialogueModeWindow).TotalSeconds}s");
        }

        public bool IsInDialogueMode => DateTime.UtcNow < _dialogueModeUntilUtc;

        private bool IsContextuallyAddressed(string text)
        {
            // Modo Dialogo: qualquer fala com tamanho minimo e aceita
            if (IsInDialogueMode && text.Trim().Length >= DialogueModeMinChars)
            {
                return true;
            }

            if (DateTime.UtcNow - _lastSuccessfulCommandUtc > ContextualActivationWindow)
            {
                return false;
            }

            var normalized = NormalizeText(text);
            return ContextualActivationPhrases.Any(phrase =>
                normalized.StartsWith(phrase, StringComparison.Ordinal)
                || normalized.Contains(" " + phrase, StringComparison.Ordinal));
        }

        private bool TryDetectWakeWord(string text, bool isPartial)
        {
            var isWakeWord = ContainsWakeWord(text);
            var isContextual = !isWakeWord && !isPartial && IsContextuallyAddressed(text);

            if (!isWakeWord && !isContextual)
            {
                return false;
            }

            // Comando especial: enrollment de voz
            var normalized = NormalizeText(text);
            if (isWakeWord && !isPartial &&
                (normalized.Contains("aprenda minha voz", StringComparison.Ordinal) ||
                 normalized.Contains("aprende minha voz", StringComparison.Ordinal) ||
                 normalized.Contains("aprenda a minha voz", StringComparison.Ordinal) ||
                 normalized.Contains("registra minha voz", StringComparison.Ordinal) ||
                 normalized.Contains("registre minha voz", StringComparison.Ordinal)))
            {
                AppendVoiceLog("[Voice] Comando de enrollment detectado.");
                StartVoiceEnrollment();
                OnCommandDetected?.Invoke("Vou aprender sua voz. Pode falar normalmente por alguns segundos...");
                return true;
            }

            if (isContextual)
            {
                AppendVoiceLog($"[Voice] Ativacao contextual: {text}");
                var command = CleanCommandCandidate(text);
                if (string.IsNullOrWhiteSpace(command)) command = text.Trim();
                _lastSuccessfulCommandUtc = DateTime.UtcNow;
                _pendingReset = true;
                OnCommandDetected?.Invoke(command);
                return true;
            }

            AppendVoiceLog(isPartial
                ? $"[Voice] Wake word detectada (parcial): {text}"
                : $"[Voice] Wake word detectada: {text}");
            OnWakeWordDetected?.Invoke(text);
            _pendingReset = true;
            return true;
        }

        private void MaybeLogWakeWordCandidate(string text, bool isPartial)
        {
            var normalizedText = NormalizeText(text);
            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                return;
            }

            var canonicalText = ToWakeWordCanonicalForm(text);
            var looksRelated = _wakeWords.Any(wakeWord =>
                    normalizedText.Contains(wakeWord[..Math.Min(wakeWord.Length, 3)], StringComparison.Ordinal))
                || _wakeWordCanonicalForms.Any(canonicalWakeWord =>
                    canonicalText.Contains(canonicalWakeWord[..Math.Min(canonicalWakeWord.Length, 3)], StringComparison.Ordinal))
                || canonicalText.Contains("keit", StringComparison.Ordinal)
                || canonicalText.Contains("kate", StringComparison.Ordinal)
                || canonicalText.Contains("ket", StringComparison.Ordinal)
                || canonicalText.Contains("quent", StringComparison.Ordinal)
                || canonicalText.Contains("kent", StringComparison.Ordinal);

            if (!looksRelated)
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            if ((nowUtc - _lastWakeCandidateLogUtc).TotalMilliseconds < 700)
            {
                return;
            }

            _lastWakeCandidateLogUtc = nowUtc;
            AppendVoiceLog(isPartial
                ? $"[Voice] Candidato wake word (parcial): bruto='{text}' canonico='{canonicalText}'"
                : $"[Voice] Candidato wake word: bruto='{text}' canonico='{canonicalText}'");
        }

        private sealed class StringComparerFromNormalize : IEqualityComparer<string>
        {
            public static readonly StringComparerFromNormalize Instance = new();

            public bool Equals(string? x, string? y)
            {
                return string.Equals(NormalizeText(x ?? string.Empty), NormalizeText(y ?? string.Empty), StringComparison.Ordinal);
            }

            public int GetHashCode(string obj)
            {
                return NormalizeText(obj).GetHashCode(StringComparison.Ordinal);
            }
        }

            private void ResetCommandState()
            {
                _listeningForCommand = false;
                _hasDetectedCommandSpeech = false;
                _commandListeningStartedUtc = DateTime.MinValue;
                _lastVoiceTimeUtc = DateTime.MinValue;
                _partialCommand = string.Empty;
                _latestPartial = string.Empty;
                ClearCommandAudioBuffer();
            }

            private void AppendCommandAudio(byte[] buffer, int bytesRecorded)
            {
                if (bytesRecorded <= 0)
                {
                    return;
                }

                lock (_commandAudioSync)
                {
                    _commandAudioBuffer.Write(buffer, 0, bytesRecorded);
                }
            }

            private byte[] TakeCommandAudioSnapshot()
            {
                lock (_commandAudioSync)
                {
                    return _commandAudioBuffer.ToArray();
                }
            }

            private void ClearCommandAudioBuffer()
            {
                lock (_commandAudioSync)
                {
                    _commandAudioBuffer.SetLength(0);
                    _commandAudioBuffer.Position = 0;
                }
            }

            private async Task<string> ResolveCommandTextAsync(string voskCommand, byte[] commandAudio)
            {
                if (_commandTranscriber is null || commandAudio.Length == 0)
                {
                    return voskCommand;
                }

                // Se o Vosk já capturou texto realmente longo, pode usar direto para reduzir latência.
                // Para comandos curtos/medios, priorizamos Whisper porque é mais preciso.
                if (voskCommand.Trim().Length >= 60)
                {
                    AppendVoiceLog($"[Voice] Usando Vosk diretamente (texto suficiente: {voskCommand.Length} chars)");
                    return voskCommand;
                }

                try
                {
                    // Timeout de 15s para evitar que Whisper trave indefinidamente
                    using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var whisperCommand = await _commandTranscriber.TranscribeAsync(commandAudio, timeoutCts.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(whisperCommand))
                    {
                        AppendVoiceLog("[Voice] Whisper retornou vazio. Usando Vosk.");
                        return voskCommand;
                    }

                    var cleanedWhisperCommand = CleanCommandCandidate(whisperCommand);
                    if (string.IsNullOrWhiteSpace(cleanedWhisperCommand))
                    {
                        return voskCommand;
                    }

                    AppendVoiceLog($"[Voice] Comando refinado por Whisper local: {cleanedWhisperCommand}");
                    return cleanedWhisperCommand;
                }
                catch (OperationCanceledException)
                {
                    AppendVoiceLog("[Voice] Whisper excedeu timeout (15s). Usando Vosk.");
                    return voskCommand;
                }
                catch (Exception ex)
                {
                    AppendVoiceLog($"[Voice] Falha na transcricao local do comando. Usando Vosk. {ex.Message}");
                    return voskCommand;
                }
            }

        private string ExtractText(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("text", out var textElement))
                {
                    return textElement.GetString() ?? string.Empty;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private string ExtractPartial(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("partial", out var partialElement))
                {
                    return partialElement.GetString() ?? string.Empty;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string NormalizeText(string text)
        {
            return text
                .Trim()
                .ToLowerInvariant()
                .Replace('á', 'a')
                .Replace('à', 'a')
                .Replace('â', 'a')
                .Replace('ã', 'a')
                .Replace('é', 'e')
                .Replace('ê', 'e')
                .Replace('í', 'i')
                .Replace('ó', 'o')
                .Replace('ô', 'o')
                .Replace('õ', 'o')
                .Replace('ú', 'u')
                .Replace('ç', 'c');
        }

        private static string ToWakeWordCanonicalForm(string text)
        {
            var normalized = NormalizeText(text);
            normalized = Regex.Replace(normalized, @"[^a-z\s]", " ", RegexOptions.CultureInvariant);
            normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

            var squashed = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
            squashed = squashed
                .Replace("quente", "keite", StringComparison.Ordinal)
                .Replace("quenti", "keite", StringComparison.Ordinal)
                .Replace("quenty", "keite", StringComparison.Ordinal)
                .Replace("kente", "keite", StringComparison.Ordinal)
                .Replace("kenti", "keite", StringComparison.Ordinal)
                .Replace("kent", "keite", StringComparison.Ordinal)
                .Replace("caite", "keite", StringComparison.Ordinal)
                .Replace("caeti", "keite", StringComparison.Ordinal)
                .Replace("cate", "keite", StringComparison.Ordinal)
                .Replace("keit", "keite", StringComparison.Ordinal)
                .Replace("keiti", "keite", StringComparison.Ordinal)
                .Replace("keyti", "keite", StringComparison.Ordinal)
                .Replace("queiti", "keite", StringComparison.Ordinal)
                .Replace("queite", "keite", StringComparison.Ordinal)
                .Replace("queyti", "keite", StringComparison.Ordinal)
                .Replace("queyte", "keite", StringComparison.Ordinal)
                .Replace("keiti", "keite", StringComparison.Ordinal)
                .Replace("kayte", "keite", StringComparison.Ordinal)
                .Replace("keyte", "keite", StringComparison.Ordinal);

            if (string.Equals(squashed, "kate", StringComparison.Ordinal))
            {
                return "keite";
            }

            return squashed;
        }

        private static string[] ResolveWakeWords()
        {
            var configuredWakeWords = Environment.GetEnvironmentVariable("KATE_WAKE_WORDS");
            if (string.IsNullOrWhiteSpace(configuredWakeWords))
            {
                return DefaultWakeWords;
            }

            var parsedWakeWords = configuredWakeWords
                .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeText)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return parsedWakeWords.Length > 0 ? parsedWakeWords : DefaultWakeWords;
        }

        private static string BuildWakeWordCleanupPattern(IEnumerable<string> wakeWords)
        {
            var escapedWakeWords = wakeWords
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .SelectMany(ExpandWakeWordCleanupTerms)
                .Select(Regex.Escape)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (escapedWakeWords.Length == 0)
            {
                escapedWakeWords = DefaultWakeWords.Select(Regex.Escape).ToArray();
            }

            return $@"^(?:{string.Join("|", escapedWakeWords)})[\s,.:;!?-]*";
        }

        private static IEnumerable<string> ExpandWakeWordCleanupTerms(string wakeWord)
        {
            var normalizedWakeWord = NormalizeText(wakeWord);
            if (string.IsNullOrWhiteSpace(normalizedWakeWord))
            {
                yield break;
            }

            yield return normalizedWakeWord;

            if (string.Equals(ToWakeWordCanonicalForm(normalizedWakeWord), "keite", StringComparison.Ordinal))
            {
                yield return "keite";
                yield return "keiti";
                yield return "keiti";
                yield return "quente";
                yield return "quenti";
                yield return "quen te";
                yield return "quen ti";
                yield return "kente";
                yield return "kenti";
                yield return "cague";
                yield return "ca gue";
                yield return "kague";
                yield return "ka gue";
                yield return "cate";
                yield return "caite";
                yield return "kayte";
                yield return "queite";
                yield return "queiti";
                yield return "kei te";
                yield return "kei ti";
                yield return "quei te";
                yield return "quei ti";
            }
        }

        private bool _disposed = false;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try {
                if (_waveIn != null)
                {
                    try { _waveIn.StopRecording(); } catch (Exception ex) { LogDisposeError("waveIn.StopRecording", ex); }
                    try { _waveIn.DataAvailable -= OnDataAvailable; } catch (Exception ex) { LogDisposeError("waveIn.RemoveHandler", ex); }
                    try { _waveIn.Dispose(); } catch (Exception ex) { LogDisposeError("waveIn.Dispose", ex); }
                }
            } catch (Exception ex) { LogDisposeError("waveIn bloco", ex); }
            try { _commandRecognizer?.Dispose(); } catch (Exception ex) { LogDisposeError("commandRecognizer", ex); }
            try { _commandModel?.Dispose(); } catch (Exception ex) { LogDisposeError("commandModel", ex); }
            try { _commandTranscriber?.Dispose(); } catch (Exception ex) { LogDisposeError("commandTranscriber", ex); }
            try { _wakeRecognizer?.Dispose(); } catch (Exception ex) { LogDisposeError("wakeRecognizer", ex); }
            try { _wakeModel?.Dispose(); } catch (Exception ex) { LogDisposeError("wakeModel", ex); }
        }

        private void LogDisposeError(string part, Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText("erro_kate.txt", $"[Dispose] Erro ao liberar {part}: {ex}\n");
            }
            catch { }
        }
    }
}

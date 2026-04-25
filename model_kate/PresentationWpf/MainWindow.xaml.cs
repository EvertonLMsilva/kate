

	using System.Windows;
	using System.Windows.Controls;
	using System.Windows.Input;
	using System.Windows.Media;
	using System.Windows.Shapes;
	using model_kate.Application;
	using model_kate.Infrastructure.Diagnostics;
	using model_kate.Infrastructure;
	using System;
	using System.Collections.Generic;
	using System.Runtime.InteropServices;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Windows.Interop;
	using model_kate.Voice;

	namespace model_kate.PresentationWpf
	{
		public partial class MainWindow : Window
		{
			[DllImport("user32.dll")]
			private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

			[DllImport("user32.dll")]
			private static extern bool SetForegroundWindow(IntPtr hWnd);

			private const int SwRestore = 9;

			private readonly GenerateResponseUseCase _useCase;
			private ITextToSpeechService? _tts;
			private VoskWakeWordRecognitionService? _voiceService;
			private readonly SemaphoreSlim _promptExecutionGate = new(1, 1);
			private readonly SemaphoreSlim _ttsInitializationGate = new(1, 1);
			private bool _isClosing;
			private bool _voiceInitializationStarted;
			private string? _ttsDescription;

		private CancellationTokenSource _promptCts = new();

		// Palavras que acionam o cancelamento imediato da resposta em curso
		private static readonly string[] StopCommands =
			["para", "stop", "cancela", "cala boca", "silêncio", "silencio", "chega", "pare"];

		// Waveform
		private readonly List<Rectangle> _waveformBars = new();
		private readonly List<SolidColorBrush> _waveformBrushes = new();
		private System.Windows.Threading.DispatcherTimer? _waveformTimer;
		private volatile int _waveformStateInt = 0; // 0=Idle 1=Listening 2=Thinking 3=Speaking
		private double _waveformTime = 0;
		private readonly Random _waveRng = new();

		private enum AIVisualState { Idle, Listening, Thinking, Speaking }

		public MainWindow()
		{
				try
				{
					InitializeComponent();
					_useCase = new GenerateResponseUseCase(new OllamaGenerativeAIService());
					Loaded += MainWindow_Loaded;
					ContentRendered += MainWindow_ContentRendered;
					Activated += MainWindow_Activated;
					SourceInitialized += MainWindow_SourceInitialized;
				}
				catch (Exception ex)
				{
					try
					{
						System.IO.File.WriteAllText("erro_kate.txt", $"Erro ao iniciar a aplicação:\n{ex}");
					}
					catch {}
					MessageBox.Show($"Erro ao iniciar a aplicação:\n{ex}", "Erro crítico", MessageBoxButton.OK, MessageBoxImage.Error);
					throw;
				}
			}
		private void MainWindow_SourceInitialized(object? sender, EventArgs e)
		{
			Visibility = Visibility.Visible;
			WindowState = WindowState.Normal;
			ShowInTaskbar = true;

			var handle = new WindowInteropHelper(this).Handle;
			if (handle != IntPtr.Zero)
			{
				ShowWindow(handle, SwRestore);
				SetForegroundWindow(handle);
			}
		}

		private void MainWindow_Loaded(object sender, RoutedEventArgs e)
		{
			LogFile.AppendLine("[UI] MainWindow carregada.");
			SetListeningState(false, "Inicializando interface...");
		}

		private async void MainWindow_ContentRendered(object? sender, EventArgs e)
		{
			if (_voiceInitializationStarted || _isClosing)
			{
				return;
			}

			_voiceInitializationStarted = true;
			LogFile.AppendLine("[UI] MainWindow renderizada. Inicializando voz em segundo plano.");

			InitWaveform();
			await Task.Yield();
			_ = InitializeTextToSpeechAsync();
			await InitializeVoiceAsync();
		}

		private void MainWindow_Activated(object? sender, EventArgs e)
		{
			LogFile.AppendLine("[UI] MainWindow ativada.");
		}

		private async Task InitializeVoiceAsync()
		{
			try
			{
				LogFile.AppendLine($"[Log] Arquivo ativo: {LogFile.PrimaryPath}");
				// Caminho do modelo Vosk: sempre ao lado do executável
				var exeDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
					?? AppContext.BaseDirectory;
				var wakeWordModelPath = ResolveWakeWordModelPath(exeDir);
				var commandModelPath = ResolveCommandModelPath(exeDir, wakeWordModelPath);
				if (string.IsNullOrWhiteSpace(wakeWordModelPath))
				{
					System.Windows.MessageBox.Show(
						$"Nenhum modelo Vosk suportado foi encontrado em '{exeDir}'.\n\n" +
						"Pastas aceitas para wake word e comando:\n" +
						"- vosk-model-pt-fb-v0.1.1-20220516_2113\n" +
						"- vosk-model-small-pt-0.3",
						"Erro de modelo",
						MessageBoxButton.OK,
						MessageBoxImage.Error);
					System.Windows.Application.Current.Shutdown();
					return;
				}

				await PostToUiAsync(() => SetListeningState(false, "Inicializando reconhecimento de voz..."));
				LogFile.AppendLine($"[Voice] Modelo Vosk wake word: {wakeWordModelPath}");
				LogFile.AppendLine($"[Voice] Modelo Vosk comando: {commandModelPath}");
				if (string.Equals(wakeWordModelPath, commandModelPath, StringComparison.OrdinalIgnoreCase))
				{
					LogFile.AppendLine("[Voice] Modelo grande desativado no desktop. Usando modelo leve tambem para comando.");
				}
				try {
					_voiceService = await Task.Run(() => new VoskWakeWordRecognitionService(wakeWordModelPath, commandModelPath));
				} catch (Exception ex) {
					System.IO.File.AppendAllText("erro_kate.txt", $"[Loaded] ERRO ao inicializar serviço de voz:\n{ex}\n");
					System.Windows.MessageBox.Show($"Erro ao inicializar serviço de voz:\n{ex.Message}", "Erro de voz", MessageBoxButton.OK, MessageBoxImage.Error);
					System.Windows.Application.Current.Shutdown();
					return;
				}
				_voiceService.OnWakeWordDetected += (wakeText) =>
				{
					if (_isClosing)
					{
						return;
					}

					PostToUi(() =>
					{
						RecognizedTextBox.Text = wakeText;
						CommandTextBox.Text = string.Empty;
						ResponseTextBox.Text = string.Empty;
						SetListeningState(false, "Ouvindo...");
					});

					_ = ConfirmWakeWordAsync().ContinueWith(t =>
					{
						if (t.IsFaulted) System.IO.File.AppendAllText("erro_kate.txt", $"[Voice] ConfirmWakeWordAsync erro: {t.Exception}\n");
					}, TaskContinuationOptions.OnlyOnFaulted);
				};

				_voiceService.OnTextRecognized += (text) =>
				{
					if (_isClosing)
					{
						return;
					}

					PostToUi(() =>
					{
						RecognizedTextBox.Text = text;
						DebugTextBlock.Text = _voiceService is not null && IsListeningForCommand()
							? $"Comando ouvido: {text}"
							: $"Texto reconhecido: {text}";
						if (_voiceService is not null && IsListeningForCommand())
						{
							CommandTextBox.Text = text;
						}
					});
				};
				_voiceService.OnCommandCanceled += () =>
				{
					if (_isClosing)
					{
						return;
					}

					PostToUi(() =>
					{
						CommandTextBox.Text = string.Empty;
						PromptTextBox.Text = string.Empty;
						DebugTextBlock.Text = "Nenhum comando detectado. Aguardando palavra de ativação.";
						SetListeningState(false, "Aguardando palavra de ativação");
					});
				};
				_voiceService.OnCommandDetected += async (command) =>
				{
					if (_isClosing) return;

					// Detecta comando de stop — cancela o que estiver em execução
					var cmd = command?.Trim() ?? string.Empty;
					if (IsStopCommand(cmd))
					{
						CancelCurrentPrompt();
						await PostToUiAsync(() =>
						{
							ResponseTextBox.Text = string.Empty;
							CommandTextBox.Text = string.Empty;
							PromptTextBox.Text = string.Empty;
							_waveformStateInt = 0;
							SetAIVisualState(AIVisualState.Idle);
							SetListeningState(false, "Pronta. Aguardando comando.");
						});
						return;
					}

					await PostToUiAsync(() =>
					{
						CommandTextBox.Text = command;
						PromptTextBox.Text = command;
						DebugTextBlock.Text = $"Enviando ao LLM: {command}";
						ResponseTextBox.Text = "Processando...";
					});
					_ = RunPromptAsync(command).ContinueWith(t =>
					{
						if (t.IsFaulted) System.IO.File.AppendAllText("erro_kate.txt", $"[Voice] RunPromptAsync erro: {t.Exception}\n");
					}, TaskContinuationOptions.OnlyOnFaulted);
				};
				_voiceService.OnStopRequested += () =>
				{
					if (_isClosing) return;
					CancelCurrentPrompt();
					PostToUi(() =>
					{
						ResponseTextBox.Text = string.Empty;
						CommandTextBox.Text = string.Empty;
						PromptTextBox.Text = string.Empty;
						_waveformStateInt = 0;
						SetAIVisualState(AIVisualState.Idle);
						SetListeningState(false, "Pronta. Aguardando comando.");
					});
				};
				_voiceService.Start();
				await PostToUiAsync(() => SetListeningState(false, "Aguardando palavra de ativação"));
			}
			catch (Exception ex)
			{
				System.IO.File.AppendAllText("erro_kate.txt", $"[Loaded] Erro ao inicializar reconhecimento de voz:\n{ex}\n");
				System.Windows.MessageBox.Show($"Erro ao inicializar reconhecimento de voz:\n{ex.Message}", "Erro de voz", MessageBoxButton.OK, MessageBoxImage.Error);
				System.Windows.Application.Current.Shutdown();
			}
		}


		private void MicButton_Click(object sender, RoutedEventArgs e)
		{
			// modo contínuo — sem ação
		}

		private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (e.ClickCount == 2)
				WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
			else
				DragMove();
		}

		private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
		private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

		private void InitWaveform()
		{
			const int barCount = 28;
			const double barW = 7, gap = 6;
			const double totalW = barCount * barW + (barCount - 1) * gap;
			var startX = (WaveformCanvas.Width - totalW) / 2.0;

			for (int i = 0; i < barCount; i++)
			{
				var brush = new SolidColorBrush(Color.FromArgb(80, 0, 70, 100));
				var bar = new Rectangle
				{
					Width = barW,
					Height = 4,
					Fill = brush,
					RadiusX = 2,
					RadiusY = 2
				};
				Canvas.SetLeft(bar, startX + i * (barW + gap));
				Canvas.SetBottom(bar, 0);
				WaveformCanvas.Children.Add(bar);
				_waveformBars.Add(bar);
				_waveformBrushes.Add(brush);
			}

			_waveformTimer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(50)
			};
			_waveformTimer.Tick += UpdateWaveform;
			_waveformTimer.Start();
		}

		private void UpdateWaveform(object? sender, EventArgs e)
		{
			_waveformTime += 0.055;
			var state = _waveformStateInt;
			var n = _waveformBars.Count;
			var canH = Math.Max(WaveformCanvas.ActualHeight, 64);

			Color color = state switch
			{
				1 => Color.FromArgb(200, 0, 210, 255),
				2 => Color.FromArgb(200, 140, 55, 220),
				3 => Color.FromArgb(220, 0, 220, 120),
				_ => Color.FromArgb(70, 0, 80, 110)
			};

			for (int i = 0; i < n; i++)
			{
				double phase = i * Math.PI / Math.Max(n - 1, 1);
				double h = state switch
				{
					0 => 3 + Math.Abs(Math.Sin(_waveformTime * 0.55 + phase)) * 4,
					1 => 5 + Math.Abs(Math.Sin(_waveformTime * 2.8 + phase * 1.4)) * 18 + _waveRng.NextDouble() * 8,
					2 => 4 + Math.Abs(Math.Sin(_waveformTime * 1.9 + phase)) * 24 * Math.Abs(Math.Sin(_waveformTime * 0.4 + i * 0.15)),
					3 => 6 + _waveRng.NextDouble() * canH * 0.7,
					_ => 3
				};
				_waveformBars[i].Height = Math.Max(3, Math.Min(h, canH - 2));
				_waveformBrushes[i].Color = color;
			}
		}

		private void SetAIVisualState(AIVisualState state)
		{
			var (ring, glow, orbMid) = state switch
			{
				AIVisualState.Listening => (
					Color.FromRgb(0, 200, 255),
					Color.FromArgb(70, 0, 200, 255),
					Color.FromRgb(0, 200, 255)),
				AIVisualState.Thinking => (
					Color.FromRgb(140, 55, 220),
					Color.FromArgb(70, 140, 55, 220),
					Color.FromRgb(140, 55, 220)),
				AIVisualState.Speaking => (
					Color.FromRgb(0, 210, 110),
					Color.FromArgb(70, 0, 210, 110),
					Color.FromRgb(0, 210, 110)),
				_ => (
					Color.FromRgb(12, 32, 50),
					Color.FromArgb(35, 0, 130, 160),
					Color.FromRgb(0, 212, 255)),
			};

			RingOuter.Stroke = new SolidColorBrush(ring);
			RingMiddle.Stroke = new SolidColorBrush(Color.FromArgb(180, ring.R, ring.G, ring.B));
			OrbGlow.Fill = new RadialGradientBrush(
				Color.FromArgb(glow.A, glow.R, glow.G, glow.B),
				Colors.Transparent);
			CentralOrb.Fill = new RadialGradientBrush(new GradientStopCollection
			{
				new GradientStop(Colors.White, 0),
				new GradientStop(orbMid, 0.42),
				new GradientStop(Color.FromRgb(8, 24, 42), 1)
			});
		}

		private void SendButton_Click(object sender, RoutedEventArgs e)
		{
			var prompt = PromptTextBox.Text;
			if (string.IsNullOrWhiteSpace(prompt))
			{
				DebugTextBlock.Text = "Digite ou fale um texto antes de enviar.";
				return;
			}

			CommandTextBox.Text = prompt.Trim();
			DebugTextBlock.Text = $"Enviando ao LLM: {prompt.Trim()}";
			ResponseTextBox.Text = "Processando...";
			_ = RunPromptAsync(prompt);
		}

		private void SettingsButton_Click(object sender, RoutedEventArgs e)
		{
			var win = new AudioSettingsWindow();
			win.Owner = this;
			win.ShowDialog();
		}
		protected override void OnClosed(System.EventArgs e)
		{
			_isClosing = true;
			try { _voiceService?.Stop(); } catch (Exception ex) { System.IO.File.AppendAllText("erro_kate.txt", $"[OnClosed] Erro ao parar voz: {ex}\n"); }
			try { _voiceService?.Dispose(); } catch (Exception ex) { System.IO.File.AppendAllText("erro_kate.txt", $"[OnClosed] Erro ao liberar voz: {ex}\n"); }
			try
			{
				// Fecha sessão no banco com resumo simples
				if (_useCase.AiService is model_kate.Infrastructure.OllamaGenerativeAIService ollamaSvc)
				{
					var summary = $"Sessão encerrada em {DateTime.Now:dd/MM/yyyy HH:mm}.";
					ollamaSvc.CloseCurrentSession(summary);
				}
			}
			catch { }
			base.OnClosed(e);
		}

		private static bool IsStopCommand(string text)
		{
			var lower = text.ToLowerInvariant().Trim();
			return Array.Exists(StopCommands, s => lower == s || lower.StartsWith(s + " ", StringComparison.Ordinal));
		}

		private void CancelCurrentPrompt()
		{
			var old = Interlocked.Exchange(ref _promptCts, new CancellationTokenSource());
			try { old.Cancel(); } catch { }
			try { old.Dispose(); } catch { }
		}

		private async Task RunPromptAsync(string prompt)
		{
			if (_isClosing) return;

			// Cancela qualquer execução anterior antes de começar a nova
			CancelCurrentPrompt();
			var cts = _promptCts;

			await _promptExecutionGate.WaitAsync();
			try
			{
				if (_isClosing || cts.IsCancellationRequested) return;
				await ExecutePromptAsync(prompt, cts.Token);
			}
			finally
			{
				_promptExecutionGate.Release();
			}
		}

		private async Task ExecutePromptAsync(string prompt, CancellationToken cancellationToken = default)
		{
			var normalizedPrompt = prompt?.Trim() ?? string.Empty;
			if (_isClosing || string.IsNullOrWhiteSpace(normalizedPrompt))
			{
				return;
			}

			LogFile.AppendLine($"[UI] Executando prompt: {normalizedPrompt}");

			await PostToUiAsync(() =>
			{
				PromptTextBox.Text = normalizedPrompt;
				CommandTextBox.Text = normalizedPrompt;
				ResponseTextBox.Text = string.Empty;
				_waveformStateInt = 2;
				SetAIVisualState(AIVisualState.Thinking);
				SetListeningState(false, "pensando...");
			});

			try
			{
				var rawResponse = await _useCase.ExecuteAsync(normalizedPrompt, token =>
				{
					PostToUi(() =>
					{
						ResponseTextBox.Text += token;
						ResponseTextBox.ScrollToEnd();
					});
				}, cancellationToken);

				if (cancellationToken.IsCancellationRequested) return;

				var narratedResponse = NormalizeNarratedText(rawResponse);

				await PostToUiAsync(() =>
				{
					ResponseTextBox.Text = rawResponse;
					PromptTextBox.Text = string.Empty;
					_waveformStateInt = 3;
					SetAIVisualState(AIVisualState.Speaking);
					DebugTextBlock.Text = string.Empty;
				});

				if (!_isClosing && !cancellationToken.IsCancellationRequested)
				{
					await SpeakWithCaptureSuppressedAsync(narratedResponse, cancellationToken);
				}

				// Entra em modo dialogo: proxima fala e aceita sem wake word
				_voiceService?.EnterDialogueMode();

				await PostToUiAsync(() =>
				{
					_waveformStateInt = 0;
					SetAIVisualState(AIVisualState.Idle);
					SetListeningState(false, "Em conversa \u2014 pode falar \uD83D\uDFE2");
				});
			}
			catch (OperationCanceledException)
			{
				// Cancelado pelo usuário — volta ao idle silenciosamente
				await PostToUiAsync(() =>
				{
					_waveformStateInt = 0;
					SetAIVisualState(AIVisualState.Idle);
					SetListeningState(false, "Pronta. Aguardando comando.");
				});
			}
			catch (Exception ex)
			{
				try { System.IO.File.AppendAllText("erro_kate.txt", $"[UI] Erro ao gerar resposta: {ex}\n"); } catch { }

				await PostToUiAsync(() =>
				{
					ResponseTextBox.Text = "Não consegui gerar a resposta.";
					_waveformStateInt = 0;
					SetAIVisualState(AIVisualState.Idle);
					SetListeningState(false, "Falha ao gerar resposta");
				});
			}
		}

		private void PostToUi(Action action)
		{
			if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
			{
				return;
			}

			try
			{
				if (Dispatcher.CheckAccess())
				{
					action();
					return;
				}

				_ = Dispatcher.InvokeAsync(() =>
				{
					if (!_isClosing)
					{
						action();
					}
				});
			}
			catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
			{
			}
		}

		private async Task PostToUiAsync(Action action)
		{
			if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
			{
				return;
			}

			try
			{
				if (Dispatcher.CheckAccess())
				{
					action();
					return;
				}

				await Dispatcher.InvokeAsync(() =>
				{
					if (!_isClosing)
					{
						action();
					}
				});
			}
			catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
			{
			}
		}

		private void SetListeningState(bool isListeningForCommand, string statusMessage)
		{
			DebugTextBlock.Text = statusMessage;
			MicButton.Content = isListeningForCommand ? "Ouvindo" : "Microfone";
			MicButton.Background = isListeningForCommand
				? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 92, 54))
				: new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(216, 209, 197));
			MicButton.Foreground = System.Windows.Media.Brushes.White;
			RecordingIndicator.Visibility = isListeningForCommand
				? Visibility.Visible
				: Visibility.Collapsed;

			_waveformStateInt = isListeningForCommand ? 1 : 0;
			SetAIVisualState(isListeningForCommand ? AIVisualState.Listening : AIVisualState.Idle);
		}

		private bool IsListeningForCommand()
		{
			return RecordingIndicator.Visibility == Visibility.Visible;
		}

		private async Task ConfirmWakeWordAsync()
		{
			try
			{
				if (_isClosing)
				{
					return;
				}

				_voiceService?.BeginCommandListening();
				await PostToUiAsync(() => SetListeningState(true, "Aguardando comando..."));
			}
			catch (Exception ex)
			{
				try { System.IO.File.AppendAllText("erro_kate.txt", $"[UI] Erro ao confirmar wake word: {ex}\n"); } catch { }
			}
		}

		private async Task SpeakWithCaptureSuppressedAsync(string text, CancellationToken cancellationToken = default)
		{
			if (_isClosing || _voiceService is null || string.IsNullOrWhiteSpace(text) || cancellationToken.IsCancellationRequested)
			{
				return;
			}

			var tts = await InitializeTextToSpeechAsync();
			if (tts is null)
			{
				return;
			}

			_voiceService.BeginCaptureSuppression();
			try
			{
				await tts.SpeakAsync(text, cancellationToken);
			}
			catch (OperationCanceledException) { /* stop acionado */ }
			finally
			{
				_voiceService.EndCaptureSuppression(TimeSpan.FromMilliseconds(300));
			}
		}

		private async Task<ITextToSpeechService?> InitializeTextToSpeechAsync()
		{
			if (_tts is not null)
			{
				return _tts;
			}

			await _ttsInitializationGate.WaitAsync();
			try
			{
				if (_tts is not null)
				{
					return _tts;
				}

				var ttsSelection = await Task.Run(() => TextToSpeechServiceFactory.Create(AppContext.BaseDirectory));
				_tts = ttsSelection.Service;
				_ttsDescription = ttsSelection.Description;
				LogFile.AppendLine($"[Voice] Narrador selecionado: {_ttsDescription}");
				return _tts;
			}
			catch (Exception ex)
			{
				LogFile.AppendLine($"[Voice] Falha ao inicializar narrador: {ex.Message}");
				return null;
			}
			finally
			{
				_ttsInitializationGate.Release();
			}
		}

		private static string NormalizeNarratedText(string? rawResponse)
		{
			if (string.IsNullOrWhiteSpace(rawResponse))
			{
				return "Não consegui gerar uma resposta.";
			}

			// Remove blocos de código para o TTS não ler caracteres de programação
			var withoutCode = System.Text.RegularExpressions.Regex.Replace(
				rawResponse,
				@"```[\s\S]*?```",
				"[código gerado]",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase);

			// Remove negrito/itálico markdown
			withoutCode = System.Text.RegularExpressions.Regex.Replace(withoutCode, @"\*{1,3}([^*]+)\*{1,3}", "$1");

			var normalized = withoutCode.Replace("\r\n", "\n").Replace("\r", "\n");
			var lines = normalized
				.Split('\n', StringSplitOptions.RemoveEmptyEntries)
				.Select(line => line.Trim())
				.Where(line => line.Length > 0);

			return string.Join(Environment.NewLine + Environment.NewLine, lines);
		}

		private static string? ResolveWakeWordModelPath(string exeDir)
		{
			return ResolveVoskModelPath(exeDir, new[]
			{
				"vosk-model-small-pt-0.3",
				"vosk-model-pt-fb-v0.1.1-20220516_2113"
			});
		}

		private static string? ResolveCommandModelPath(string exeDir, string? fallbackPath)
		{
			if (!ShouldUseBigCommandModel())
			{
				return fallbackPath;
			}

			return ResolveVoskModelPath(exeDir, new[]
			{
				"vosk-model-pt-fb-v0.1.1-20220516_2113",
				"vosk-model-small-pt-0.3"
			}) ?? fallbackPath;
		}

		private static bool ShouldUseBigCommandModel()
		{
			var value = Environment.GetEnvironmentVariable("KATE_ENABLE_BIG_VOSK_COMMAND_MODEL");
			return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
		}

		private static string? ResolveVoskModelPath(string exeDir, string[] candidateDirectories)
		{
			var searchRoots = GetSearchRoots(exeDir);

			foreach (var searchRoot in searchRoots)
			{
				foreach (var candidateDirectory in candidateDirectories)
				{
					var candidatePaths = new[]
					{
						System.IO.Path.Combine(searchRoot, candidateDirectory),
						System.IO.Path.Combine(searchRoot, "documentacao", candidateDirectory),
						System.IO.Path.Combine(searchRoot, "documentacao", candidateDirectory, candidateDirectory)
					};

					foreach (var candidatePath in candidatePaths)
					{
						if (IsValidVoskModelPath(candidatePath))
						{
							return candidatePath;
						}
					}
				}
			}

			return null;
		}

		private static string[] GetSearchRoots(string exeDir)
		{
			var roots = new System.Collections.Generic.List<string>();
			var current = new System.IO.DirectoryInfo(exeDir);

			while (current is not null)
			{
				roots.Add(current.FullName);
				current = current.Parent;
			}

			return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		}

		private static bool IsValidVoskModelPath(string candidatePath)
		{
			if (!System.IO.Directory.Exists(candidatePath))
			{
				return false;
			}

			var serverModelPaths = new[]
			{
				System.IO.Path.Combine(candidatePath, "am", "final.mdl"),
				System.IO.Path.Combine(candidatePath, "conf", "model.conf")
			};

			if (serverModelPaths.All(System.IO.File.Exists))
			{
				return true;
			}

			var desktopModelPaths = new[]
			{
				System.IO.Path.Combine(candidatePath, "final.mdl"),
				System.IO.Path.Combine(candidatePath, "mfcc.conf"),
				System.IO.Path.Combine(candidatePath, "phones.txt")
			};

			return desktopModelPaths.All(System.IO.File.Exists);
		}



		// Removido: toda a lógica de WebSocket e polling

		// O botão de microfone pode ser removido ou desabilitado, pois agora é contínuo





	}
}
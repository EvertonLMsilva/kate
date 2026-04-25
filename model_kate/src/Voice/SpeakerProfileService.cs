using System;
using System.IO;
using System.Text.Json;
using NAudio.Dsp;

namespace model_kate.Voice
{
    /// <summary>
    /// Serviço de identificação de locutor baseado em características espectrais simples.
    /// Usa espectro de frequência (FFT), RMS e ZCR para criar uma "impressão digital" de voz.
    /// 
    /// Fluxo:
    ///   1. Chame EnrollFromPcm() com ~3-5s de áudio do usuário para registrar o perfil.
    ///   2. Chame Verify() a cada chunk de áudio — retorna true se for o locutor registrado.
    ///   3. O perfil é salvo em speaker_profile.json automaticamente.
    /// </summary>
    public sealed class SpeakerProfileService
    {
        private const int FrameSize = 512;           // amostras por frame (16kHz → ~32ms)
        private const int FeatureCount = 6;          // rms, zcr, sc, sr, sf, pitch
        private const float AcceptThreshold = 3.2f;  // distância Mahalanobis máxima (mais alto = mais tolerante)
        private const int MinFramesForEnroll = 30;   // ~1 segundo mínimo para enroll

        private readonly string _profilePath;

        private float[]? _mean;   // média de cada feature
        private float[]? _invStd; // 1 / desvio padrão de cada feature (para Mahalanobis)

        public bool IsEnrolled => _mean != null;
        public bool VerificationEnabled { get; set; } = true;

        public SpeakerProfileService(string? profilePath = null)
        {
            _profilePath = profilePath ?? Path.Combine(Directory.GetCurrentDirectory(), "speaker_profile.json");
            TryLoadProfile();
        }

        // ── API pública ───────────────────────────────────────────────────────

        /// <summary>Registra o perfil do locutor a partir de áudio PCM16 16kHz.</summary>
        public bool EnrollFromPcm(byte[] pcmBuffer, int bytesRecorded)
        {
            var samples = PcmToFloats(pcmBuffer, bytesRecorded);
            var matrix = ExtractFeatureMatrix(samples);
            if (matrix.Length < MinFramesForEnroll)
                return false;

            ComputeStats(matrix, out _mean, out _invStd);
            SaveProfile();
            return true;
        }

        /// <summary>Verifica se o áudio pertence ao locutor registrado. Retorna true se sim ou se não há perfil.</summary>
        public bool Verify(byte[] pcmBuffer, int bytesRecorded)
        {
            if (!IsEnrolled || !VerificationEnabled) return true;

            var samples = PcmToFloats(pcmBuffer, bytesRecorded);
            if (samples.Length < FrameSize * 2) return true; // chunk muito curto = ignora

            var matrix = ExtractFeatureMatrix(samples);
            if (matrix.Length < 5) return true; // poucos frames = ignora

            var testMean = new float[FeatureCount];
            for (int j = 0; j < FeatureCount; j++)
            {
                float sum = 0f;
                for (int i = 0; i < matrix.Length; i++) sum += matrix[i][j];
                testMean[j] = sum / matrix.Length;
            }

            var dist = MahalanobisDistance(testMean, _mean!, _invStd!);
            return dist <= AcceptThreshold;
        }

        public void ClearProfile()
        {
            _mean = null;
            _invStd = null;
            if (File.Exists(_profilePath)) File.Delete(_profilePath);
        }

        // ── Extração de features ──────────────────────────────────────────────

        private static float[][] ExtractFeatureMatrix(float[] samples)
        {
            int frames = samples.Length / FrameSize;
            if (frames == 0) return [];

            var matrix = new float[frames][];
            for (int i = 0; i < frames; i++)
            {
                int start = i * FrameSize;
                var frame = new float[FrameSize];
                Array.Copy(samples, start, frame, 0, FrameSize);
                matrix[i] = ExtractFrameFeatures(frame);
            }
            return matrix;
        }

        private static float[] ExtractFrameFeatures(float[] frame)
        {
            int n = frame.Length;

            // 1. RMS energy
            float sumSq = 0f;
            for (int i = 0; i < n; i++) sumSq += frame[i] * frame[i];
            float rms = MathF.Sqrt(sumSq / n);

            // 2. Zero crossing rate
            int zcr = 0;
            for (int i = 1; i < n; i++)
                if ((frame[i] >= 0) != (frame[i - 1] >= 0)) zcr++;
            float zcrNorm = (float)zcr / n;

            // 3. FFT para features espectrais (usa potência de 2 mais próxima)
            int fftSize = NextPowerOf2(n);
            var complex = new Complex[fftSize];
            for (int i = 0; i < fftSize; i++)
                complex[i] = new Complex { X = i < n ? frame[i] : 0f, Y = 0f };

            FastFourierTransform.FFT(true, (int)Math.Log2(fftSize), complex);

            // Magnitude do espectro (metade positiva)
            int half = fftSize / 2;
            float totalEnergy = 0f;
            var mag = new float[half];
            for (int i = 0; i < half; i++)
            {
                mag[i] = complex[i].X * complex[i].X + complex[i].Y * complex[i].Y;
                totalEnergy += mag[i];
            }

            // 4. Spectral centroid (frequência média ponderada)
            float sc = 0f;
            if (totalEnergy > 1e-8f)
            {
                for (int i = 0; i < half; i++) sc += i * mag[i];
                sc /= totalEnergy;
                sc /= half; // normalizar para [0,1]
            }

            // 5. Spectral rolloff (ponto onde 85% da energia está abaixo)
            float roloff = 0f;
            float cumEnergy = 0f;
            float targetEnergy = totalEnergy * 0.85f;
            for (int i = 0; i < half; i++)
            {
                cumEnergy += mag[i];
                if (cumEnergy >= targetEnergy) { roloff = (float)i / half; break; }
            }

            // 6. Pitch estimate via autocorrelação (faixa 80-400 Hz a 16kHz)
            int minLag = 16000 / 400;  // lag para 400Hz = 40
            int maxLag = 16000 / 80;   // lag para  80Hz = 200
            float pitch = EstimatePitch(frame, minLag, maxLag);

            return [rms, zcrNorm, sc, roloff, pitch];
        }

        private static float EstimatePitch(float[] frame, int minLag, int maxLag)
        {
            int n = frame.Length;
            float bestCorr = -1f;
            float bestLag = 0f;

            // autocorrelação normalizada
            for (int lag = minLag; lag <= Math.Min(maxLag, n / 2); lag++)
            {
                float corr = 0f;
                for (int i = 0; i < n - lag; i++) corr += frame[i] * frame[i + lag];
                if (corr > bestCorr) { bestCorr = corr; bestLag = lag; }
            }

            return bestLag > 0 ? (16000f / bestLag) / 400f : 0f; // normalizar
        }

        // ── Estatísticas ───────────────────────────────────────────────────────

        private static void ComputeStats(float[][] matrix, out float[] mean, out float[] invStd)
        {
            int n = matrix.Length;
            int f = matrix[0].Length;
            mean = new float[f];
            invStd = new float[f];

            for (int j = 0; j < f; j++)
            {
                float sum = 0f;
                for (int i = 0; i < n; i++) sum += matrix[i][j];
                mean[j] = sum / n;

                float variance = 0f;
                for (int i = 0; i < n; i++) variance += (matrix[i][j] - mean[j]) * (matrix[i][j] - mean[j]);
                variance /= n;
                float std = MathF.Sqrt(variance);
                invStd[j] = std > 1e-8f ? 1f / std : 1f;
            }
        }

        private static float MahalanobisDistance(float[] test, float[] mean, float[] invStd)
        {
            float sum = 0f;
            for (int i = 0; i < mean.Length; i++)
            {
                float diff = (test[i] - mean[i]) * invStd[i];
                sum += diff * diff;
            }
            return MathF.Sqrt(sum);
        }

        // ── Persistência ──────────────────────────────────────────────────────

        private void SaveProfile()
        {
            if (_mean == null || _invStd == null) return;
            var data = new { mean = _mean, invStd = _invStd };
            File.WriteAllText(_profilePath, JsonSerializer.Serialize(data));
        }

        private void TryLoadProfile()
        {
            try
            {
                if (!File.Exists(_profilePath)) return;
                var json = File.ReadAllText(_profilePath);
                using var doc = JsonDocument.Parse(json);
                _mean   = JsonSerializer.Deserialize<float[]>(doc.RootElement.GetProperty("mean").GetRawText());
                _invStd = JsonSerializer.Deserialize<float[]>(doc.RootElement.GetProperty("invStd").GetRawText());
            }
            catch { _mean = null; _invStd = null; }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static float[] PcmToFloats(byte[] pcmBuffer, int bytesRecorded)
        {
            int sampleCount = bytesRecorded / 2; // PCM16 = 2 bytes por sample
            var result = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                short s = BitConverter.ToInt16(pcmBuffer, i * 2);
                result[i] = s / 32768f;
            }
            return result;
        }

        private static int NextPowerOf2(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }
    }
}

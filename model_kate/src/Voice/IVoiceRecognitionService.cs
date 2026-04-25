namespace model_kate.Voice
{
    public interface IVoiceRecognitionService
    {
        /// <summary>
        /// Inicia o reconhecimento de voz e retorna o texto reconhecido.
        /// </summary>
        /// <returns>Texto reconhecido da fala.</returns>
        Task<string> RecognizeOnceAsync();
    }
}
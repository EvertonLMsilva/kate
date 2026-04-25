namespace model_kate.Voice
{
    public interface ITextToSpeechService
    {
        Task SpeakAsync(string text, CancellationToken cancellationToken = default);
    }
}
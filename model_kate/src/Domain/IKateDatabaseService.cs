using System;
using System.Collections.Generic;

namespace model_kate.Domain
{
    public sealed record ConversationRecord(
        long Id,
        long SessionId,
        DateTime Timestamp,
        string UserText,
        string KateResponse);

    public sealed record UserFact(string Key, string Value, DateTime UpdatedAt);

    public sealed record SessionRecord(
        long Id,
        DateTime StartedAt,
        DateTime? EndedAt,
        string? Summary,
        int TurnCount);

    public interface IKateDatabaseService : IDisposable
    {
        // Sessões
        long CreateSession();
        void CloseSession(long sessionId, string? summary);
        SessionRecord? GetLastSession();

        // Conversas
        void SaveTurn(long sessionId, string userText, string kateResponse);
        IReadOnlyList<ConversationRecord> GetRecentTurns(int count);
        IReadOnlyList<ConversationRecord> SearchHistory(string keyword, int maxResults = 10);

        // Fatos do usuário
        void UpsertFact(string key, string value);
        IReadOnlyList<UserFact> GetAllFacts();
        string? GetFact(string key);
    }
}

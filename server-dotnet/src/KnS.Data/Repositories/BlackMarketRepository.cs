namespace KnS.Data.Repositories;

/// <summary>When each player's Black Market stock expires. The stock itself is item rows with the shop's claimed state.</summary>
public sealed class BlackMarketRepository(DataSession session)
{
    public long GetNextRefresh(long userNumber) =>
        session.QueryFirstOrDefault<long?>(
            "SELECT next_refresh FROM black_market WHERE user_no=@userNumber", new { userNumber }) ?? 0;

    public void SetNextRefresh(long userNumber, long nextRefresh) =>
        session.Execute(
            $"{session.Dialect.InsertOrReplace} INTO black_market(user_no, next_refresh) VALUES(@userNumber, @nextRefresh)",
            new { userNumber, nextRefresh });
}

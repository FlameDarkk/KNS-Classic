using KnS.Data.Dialects;

namespace KnS.Data.Repositories;

public sealed record MailRecord
{
    public long MailNumber { get; init; }
    public long UserNumber { get; init; }
    public long ItemId1 { get; init; }
    public long Value1 { get; init; }
    public long ItemId2 { get; init; }
    public long Value2 { get; init; }
    public string Message { get; init; } = "";
    public string Title { get; init; } = "";
    public long TextCode { get; init; }
    public long Type { get; init; }
    public long RegDate { get; init; }
}

/// <summary>Persistent reward messages; claims run in the gateway transaction.</summary>
public sealed class MailRepository(DataSession session)
{
    private const string Columns = "rowid_alias AS MailNumber,user_no AS UserNumber,item_id1 AS ItemId1,value1 AS Value1,"
        + "item_id2 AS ItemId2,value2 AS Value2,message AS Message,COALESCE(title,'') AS Title,text_code AS TextCode,type AS Type,reg_date AS RegDate";

    public long Add(long userNumber, long itemId1, long value1, long itemId2 = 0, long value2 = 0,
        string message = "", long textCode = 0, long type = 2, long regDate = 0, string title = "") => session.Insert(
        "INSERT INTO reward_mail(user_no,item_id1,value1,item_id2,value2,message,title,text_code,type,reg_date)"
        + " VALUES(@userNumber,@itemId1,@value1,@itemId2,@value2,@message,@title,@textCode,@type,@regDate)",
        new { userNumber, itemId1, value1, itemId2, value2, message, title, textCode, type, regDate });

    public long Count(long userNumber, long type = 2) => session.QueryFirstOrDefault<long>(
        "SELECT COUNT(*) FROM reward_mail WHERE user_no=@userNumber AND type=@type", new { userNumber, type });

    public IReadOnlyList<MailRecord> List(long userNumber, long type, long skip, long take) => session.Query<MailRecord>(
        $"SELECT {Columns} FROM reward_mail WHERE user_no=@userNumber AND type=@type ORDER BY rowid_alias DESC LIMIT @take OFFSET @skip",
        new { userNumber, type, skip, take });

    public void LockUser(long userNumber) => session.Execute(
        "UPDATE gameuser SET gold=gold WHERE user_no=@userNumber", new { userNumber });

    public MailRecord? Find(long userNumber, long mailNumber) => session.QueryFirstOrDefault<MailRecord>(
        $"SELECT {Columns} FROM reward_mail WHERE user_no=@userNumber AND rowid_alias=@mailNumber"
        + (session.Dialect is MySqlDialect ? " FOR UPDATE" : ""), new { userNumber, mailNumber });

    public void Delete(long userNumber, long mailNumber) => session.Execute(
        "DELETE FROM reward_mail WHERE user_no=@userNumber AND rowid_alias=@mailNumber", new { userNumber, mailNumber });
}

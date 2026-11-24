namespace KnS.Data.Repositories;

/// <summary>Transactional record of each random ally grant, including duplicate results.</summary>
public sealed class FellowDrawAuditRepository(DataSession session)
{
    /// <summary>The login ID linked to the original device; guests have no account name.</summary>
    public string Username(long userNumber) => session.QueryFirstOrDefault<string>(
        "SELECT COALESCE(l.login_id,'') FROM gameuser g LEFT JOIN login_account l ON l.did=g.hub_did WHERE g.user_no=@userNumber",
        new { userNumber }) ?? "";

    public void Record(long userNumber, string username, string nickname, string batchId, long timestamp,
        string source, long packageId, long paymentItem, long paymentCount, long fellowNumber, long fellowId)
    {
        int rows = session.Execute(
            "INSERT INTO fellow_draw_audit(fellow_no,batch_id,drawn_at,user_no,username,nickname,source,package_id,payment_item,payment_count,fellow_id,fellow_name,grade)"
            + " SELECT @fellowNumber,@batchId,@timestamp,@userNumber,@username,@nickname,@source,@packageId,@paymentItem,@paymentCount,fellow_id,name,grade"
            + " FROM master_fellow WHERE fellow_id=@fellowId",
            new { fellowNumber, batchId, timestamp, userNumber, username, nickname, source, packageId, paymentItem, paymentCount, fellowId });
        if (rows != 1) throw new InvalidOperationException($"Cannot audit unknown ally {fellowId}.");
    }
}

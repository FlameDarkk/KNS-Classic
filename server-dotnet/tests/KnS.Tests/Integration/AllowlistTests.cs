using System.Text;
using System.Text.Json.Nodes;
using KnS.Protocol.Crypto;
using KnS.Protocol.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KnS.Tests.Integration;

/// <summary>the tester table (KNS_TESTERS_ONLY), on a server of its own (the setting is read when the host starts).</summary>
[Collection(SharedServer.Name)]
public sealed class AllowlistTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kns-allow-" + Guid.NewGuid().ToString("N"));

    // Runs in the shared-server collection so no other host starts while the variable is set;
    // the fixture is unused.
    public AllowlistTests(ServerFixture shared)
    {
        _ = shared;
        Directory.CreateDirectory(_folder);
        string? previousDb = Environment.GetEnvironmentVariable("KNS_DB");
        Environment.SetEnvironmentVariable("KNS_DB", Path.Combine(_folder, "kns.sqlite"));
        Environment.SetEnvironmentVariable("KNS_QUIET", "1");
        Environment.SetEnvironmentVariable("KNS_TESTERS_ONLY", "1");
        Environment.SetEnvironmentVariable("KNS_ADMIN_KEY", "secret");
        try
        {
            _factory = new WebApplicationFactory<Program>();
            _client = _factory.CreateClient();
        }
        finally
        {
            Environment.SetEnvironmentVariable("KNS_TESTERS_ONLY", null);
            Environment.SetEnvironmentVariable("KNS_ADMIN_KEY", null);
            Environment.SetEnvironmentVariable("KNS_DB", previousDb);
        }
    }

    [Fact]
    public async Task A_tester_is_refused_until_a_device_is_attached_and_again_once_revoked()
    {
        await AdminAsync("", new { email = "Friend@Example.com", note = "alpha" });

        JsonObject refused = await CallAsync(51, new { hub_did = "friend-device" });
        Assert.Equal(1, refused["errno"]!.GetValue<int>());
        Assert.False(refused.ContainsKey("user_no"));

        JsonObject overview = await AdminAsync("attach", new { email = "friend@example.com", device_id = "friend-device" });
        Assert.Empty(overview["refused"]!.AsArray());
        long user = (await CallAsync(51, new { hub_did = "friend-device" }))["user_no"]!.GetValue<long>();
        Assert.Equal(0, (await CallAsync(60, new { user_no = user }, user))["errno"]!.GetValue<int>());

        await AdminAsync("revoke", new { email = "friend@example.com" });
        Assert.Equal(1, (await CallAsync(51, new { hub_did = "friend-device" }))["errno"]!.GetValue<int>());
        Assert.Equal(1, (await CallAsync(60, new { user_no = user }, user))["errno"]!.GetValue<int>());

        await AdminAsync("restore", new { email = "friend@example.com" });
        Assert.Equal(0, (await CallAsync(51, new { hub_did = "friend-device" }))["errno"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_refused_device_is_listed_once_with_its_attempt_count()
    {
        await CallAsync(51, new { hub_did = "stranger" });
        await CallAsync(51, new { hub_did = "stranger" });

        JsonObject overview = await AdminAsync("", null, HttpMethod.Get);
        JsonNode device = overview["refused"]!.AsArray().Single(d => d!["device_id"]!.GetValue<string>() == "stranger")!;
        Assert.Equal(2, device["attempts"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_device_cannot_belong_to_two_emails()
    {
        await AdminAsync("", new { email = "a@example.com" });
        await AdminAsync("", new { email = "b@example.com" });
        await AdminAsync("attach", new { email = "a@example.com", device_id = "shared-device" });

        using HttpResponseMessage response = await SendAdminAsync("attach", new { email = "b@example.com", device_id = "shared-device" }, "secret");

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task The_admin_endpoint_needs_the_key()
    {
        using HttpResponseMessage wrong = await SendAdminAsync("", null, "nope", HttpMethod.Get);
        using HttpResponseMessage missing = await SendAdminAsync("", null, null, HttpMethod.Get);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, missing.StatusCode);
    }

    [Fact]
    public async Task The_admin_flags_endpoint_lists_and_changes_a_flag()
    {
        JsonObject created = await AdminPathAsync("/admin/flags", new { feature_key = "sample_flag", enabled = true });
        Assert.True(created["flags"]!.AsArray().Single(f => f!["feature_key"]!.GetValue<string>() == "sample_flag")!["enabled"]!.GetValue<bool>());

        JsonObject after = await AdminPathAsync("/admin/flags", new { feature_key = "sample_flag", enabled = false });
        Assert.False(after["flags"]!.AsArray().Single(f => f!["feature_key"]!.GetValue<string>() == "sample_flag")!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Account_editor_requires_key_and_persists_profile_and_inventory_edits()
    {
        await AdminAsync("", new { email = "editor@example.com" });
        await AdminAsync("attach", new { email = "editor@example.com", device_id = "editor-device" });
        long user = (await CallAsync(51, new { hub_did = "editor-device" }))["user_no"]!.GetValue<long>();
        using var unauthorized = new HttpRequestMessage(HttpMethod.Get, "/admin/accounts");
        using HttpResponseMessage rejected = await _client.SendAsync(unauthorized);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);
        await AdminPathAsync("/admin/accounts", new { action = "profile", user_no = user, nickname = "EditorTest", gold = 12345, gems = 321, level = 2, experience = 7 });
        await AdminPathAsync("/admin/accounts", new { action = "goods", user_no = user, item_id = 1900001, count = 42 });
        JsonObject snapshot = await AdminPathAsync($"/admin/accounts?user_no={user}", null, HttpMethod.Get);
        Assert.Equal("EditorTest", snapshot["account"]!["Nickname"]!.GetValue<string>());
        Assert.Equal(12345, snapshot["account"]!["Gold"]!.GetValue<long>());
        Assert.Equal(321, snapshot["account"]!["Gems"]!.GetValue<long>());
        Assert.Equal(7, snapshot["account"]!["Experience"]!.GetValue<long>());
        Assert.Equal(42, snapshot["goods"]!.AsArray().Single(g => g!["ItemId"]!.GetValue<long>() == 1900001)!["Count"]!.GetValue<long>());
        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/admin/accounts");
        invalid.Headers.Add("X-Admin-Key", "secret");
        invalid.Content = new StringContent(WireJson.ToJsonObject(new { action = "goods", user_no = user, item_id = 1900008, count = 100 }).ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage invalidReply = await _client.SendAsync(invalid);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidReply.StatusCode);
        snapshot = await AdminPathAsync($"/admin/accounts?user_no={user}", null, HttpMethod.Get);
        Assert.Equal(12345, snapshot["account"]!["Gold"]!.GetValue<long>());
        Assert.DoesNotContain(snapshot["goods"]!.AsArray(), g => g!["ItemId"]!.GetValue<long>() == 1900008);
        await AdminPathAsync("/admin/accounts", new { action = "fellow", user_no = user, item_id = 100001 });
        snapshot = await AdminPathAsync($"/admin/accounts?user_no={user}", null, HttpMethod.Get);
        Assert.Contains(snapshot["fellows"]!.AsArray(), f => f!["FellowId"]!.GetValue<long>() == 100001);
    }

    [Fact]
    public async Task Studio_grants_real_material_instances_and_validates_whole_quantity()
    {
        long user = await StudioUserAsync("grant");
        await AdminPathAsync("/admin/accounts", new { action="material", user_no=user, item_id=1600001, quantity=3 });
        JsonObject before = await StudioSnapshotAsync(user);
        Assert.Equal(3, before["items"]!.AsArray().Count(x => x!["ItemId"]!.GetValue<long>()==1600001));
        Assert.Contains("inventory_edit", before["capabilities"]!.AsArray().Select(x=>x!.GetValue<string>()));
        await StudioRefusedAsync(new { action="material", user_no=user, item_id=1900008, quantity=1 });
        await StudioRefusedAsync(new { action="item", user_no=user, item_id=200001, quantity=101 });
        await StudioRefusedAsync(new { action="fellow_item", user_no=user, item_id=200001, quantity=2 });
        JsonObject after = await StudioSnapshotAsync(user);
        Assert.Equal(before["items"]!.ToJsonString(), after["items"]!.ToJsonString());
        Assert.Equal(before["account"]!.ToJsonString(), after["account"]!.ToJsonString());
    }

    [Fact]
    public async Task Studio_item_edits_respect_ownership_upgrade_cap_lock_and_equipment()
    {
        long user=await StudioUserAsync("owner"), other=await StudioUserAsync("other");
        await AdminPathAsync("/admin/accounts",new { action="item",user_no=user,item_id=200001 });
        JsonObject before=await StudioSnapshotAsync(user);
        JsonNode item=before["items"]!.AsArray().Single(x=>x!["ItemId"]!.GetValue<long>()==200001)!;
        long number=item["ItemNumber"]!.GetValue<long>();
        await StudioRefusedAsync(new { action="item_edit",user_no=other,number,upgrade=1,locked=0 });
        await StudioRefusedAsync(new { action="item_edit",user_no=user,number,upgrade=99,locked=1 });
        Assert.Equal(before["items"]!.ToJsonString(),(await StudioSnapshotAsync(user))["items"]!.ToJsonString());
        await AdminPathAsync("/admin/accounts",new { action="item_edit",user_no=user,number,upgrade=2,locked=1 });
        JsonNode updated=(await StudioSnapshotAsync(user))["items"]!.AsArray().Single(x=>x!["ItemNumber"]!.GetValue<long>()==number)!;
        Assert.Equal(2,updated["Upgrade"]!.GetValue<long>());
        Assert.Equal(33,updated["Attack"]!.GetValue<long>());
        await StudioRefusedAsync(new { action="item_delete",user_no=user,number });
        await AdminPathAsync("/admin/accounts",new { action="item_edit",user_no=user,number,locked=0 });
        await AdminPathAsync("/admin/accounts",new { action="item_equip",user_no=user,number,hero_no=1 });
        await StudioRefusedAsync(new { action="item_delete",user_no=user,number });
        await StudioRefusedAsync(new { action="item_equip",user_no=user,number,hero_no=999 });
        await AdminPathAsync("/admin/accounts",new { action="item_equip",user_no=user,number,hero_no=0 });
        await AdminPathAsync("/admin/accounts",new { action="item_delete",user_no=user,number });
        Assert.DoesNotContain((await StudioSnapshotAsync(user))["items"]!.AsArray(),x=>x!["ItemNumber"]!.GetValue<long>()==number);
        Assert.Equal(before["account"]!.ToJsonString(),(await StudioSnapshotAsync(user))["account"]!.ToJsonString());
    }

    [Fact]
    public async Task Studio_ally_edits_protect_party_missions_and_worn_equipment()
    {
        long user=await StudioUserAsync("ally");
        await AdminPathAsync("/admin/accounts",new { action="fellow",user_no=user,item_id=100001 });
        await AdminPathAsync("/admin/accounts",new { action="fellow_item",user_no=user,item_id=1200001 });
        JsonObject initial=await StudioSnapshotAsync(user);
        long number=initial["fellows"]!.AsArray().Single(x=>x!["FellowId"]!.GetValue<long>()==100001)!["FellowNumber"]!.GetValue<long>();
        long gear=initial["fellow_items"]!.AsArray()[0]!["FellowItemNumber"]!.GetValue<long>();
        await StudioRefusedAsync(new { action="fellow_edit",user_no=user,number,level=21,experience=0,upgrade=1,locked=0 });
        await StudioRefusedAsync(new { action="fellow_edit",user_no=user,number,level=20,experience=1,upgrade=1,locked=0 });
        await AdminPathAsync("/admin/accounts",new { action="fellow_edit",user_no=user,number,level=2,experience=7,upgrade=3,locked=0 });
        JsonNode changed=(await StudioSnapshotAsync(user))["fellows"]!.AsArray().Single(x=>x!["FellowNumber"]!.GetValue<long>()==number)!;
        Assert.Equal(2,changed["Level"]!.GetValue<long>());Assert.Equal(7,changed["Experience"]!.GetValue<long>());Assert.Equal(3,changed["Upgrade"]!.GetValue<long>());
        await AdminPathAsync("/admin/accounts",new { action="fellow_slot",user_no=user,number,setup=3,position=0 });
        await StudioRefusedAsync(new { action="fellow_delete",user_no=user,number });
        await AdminPathAsync("/admin/accounts",new { action="fellow_slot",user_no=user,number,setup=3,position=-1 });
        await StudioRefusedAsync(new { action="fellow_item_equip",user_no=user,number=gear,fellow_no=number,equip_index=2 });
        await AdminPathAsync("/admin/accounts",new { action="fellow_item_equip",user_no=user,number=gear,fellow_no=number,equip_index=1 });
        await StudioRefusedAsync(new { action="fellow_item_delete",user_no=user,number=gear });
        await StudioRefusedAsync(new { action="fellow_delete",user_no=user,number });
        StudioSql($"INSERT INTO fellow_mission(user_no,mission_id,status,fellows,soldiers,end_time) VALUES({user},6200001,1,'{number}','',0)");
        await StudioRefusedAsync(new { action="fellow_edit",user_no=user,number,level=2,experience=0,upgrade=0,locked=0 });
        await StudioRefusedAsync(new { action="fellow_item_equip",user_no=user,number=gear,fellow_no=0,equip_index=1 });
        StudioSql($"DELETE FROM fellow_mission WHERE user_no={user}");
        await AdminPathAsync("/admin/accounts",new { action="fellow_item_equip",user_no=user,number=gear,fellow_no=0,equip_index=1 });
        await AdminPathAsync("/admin/accounts",new { action="fellow_item_edit",user_no=user,number=gear,locked=1 });
        await StudioRefusedAsync(new { action="fellow_item_delete",user_no=user,number=gear });
        await AdminPathAsync("/admin/accounts",new { action="fellow_item_edit",user_no=user,number=gear,locked=0 });
        await AdminPathAsync("/admin/accounts",new { action="fellow_item_delete",user_no=user,number=gear });
        await AdminPathAsync("/admin/accounts",new { action="fellow_delete",user_no=user,number });
        Assert.Empty((await StudioSnapshotAsync(user))["fellows"]!.AsArray());
        Assert.Empty((await StudioSnapshotAsync(user))["fellow_items"]!.AsArray());
    }

    [Fact]
    public async Task Studio_pinned_notices_list_edit_and_remove_and_reach_the_public_pin_feed()
    {
        await AdminPathAsync("/admin/accounts",new { action="chat_notice",message="Studio pin A",pin_seconds=600 });
        JsonObject listed=await AdminPathAsync("/admin/accounts",new { action="pins_list" });
        JsonNode pin=listed["pins"]!.AsArray().First(p=>p!["message"]!.GetValue<string>()=="Studio pin A")!;
        long id=pin["id"]!.GetValue<long>();

        JsonObject edited=await AdminPathAsync("/admin/accounts",new { action="pin_update",id,message="Studio pin B",pin_seconds=1800 });
        JsonNode after=edited["pins"]!.AsArray().Single(p=>p!["id"]!.GetValue<long>()==id)!;
        Assert.Equal("Studio pin B",after["message"]!.GetValue<string>());
        Assert.InRange(after["end_time"]!.GetValue<long>()-edited["now"]!.GetValue<long>(),1795,1800);

        using (HttpResponseMessage response=await _client.GetAsync("/api/pins"))
        {
            response.EnsureSuccessStatusCode();
            JsonArray row=JsonNode.Parse(await response.Content.ReadAsStringAsync())!["pins"]!.AsArray()
                .Single(p=>p![0]!.GetValue<long>()==id)!.AsArray();
            Assert.Equal("Studio pin B",row[3]!.GetValue<string>());
        }

        await StudioRefusedAsync(new { action="chat_notice",message="Channel pin",channel=2,pin_seconds=600 });
        await StudioRefusedAsync(new { action="pin_update",id,message="",pin_seconds=600 });
        await AdminPathAsync("/admin/accounts",new { action="pin_remove",id });
        Assert.DoesNotContain((await AdminPathAsync("/admin/accounts",new { action="pins_list" }))["pins"]!.AsArray(),p=>p!["id"]!.GetValue<long>()==id);
        await StudioRefusedAsync(new { action="pin_remove",id });
        using (HttpResponseMessage response=await _client.GetAsync("/api/pins"))
        {
            Assert.DoesNotContain(JsonNode.Parse(await response.Content.ReadAsStringAsync())!["pins"]!.AsArray(),p=>p![0]!.GetValue<long>()==id);
        }
    }

    [Fact]
    public async Task Studio_saved_notices_list_edit_and_delete()
    {
        string text=$"Studio saved notice {Guid.NewGuid():N}";
        await AdminPathAsync("/admin/accounts",new { action="chat_notice",message=text });
        JsonObject listed=await AdminPathAsync("/admin/accounts",new { action="notices_list" });
        JsonNode notice=listed["notices"]!.AsArray().Single(n=>n!["message"]!.GetValue<string>()==text)!;
        long[] ids=[.. notice["ids"]!.AsArray().Select(i=>i!.GetValue<long>())];
        Assert.NotEmpty(ids);
        Assert.Contains(1000,notice["groups"]!.AsArray().Select(g=>g!.GetValue<int>()));
        Assert.Equal(0,notice["position"]!.GetValue<long>());

        JsonObject edited=await AdminPathAsync("/admin/accounts",new { action="notice_edit",ids,message=text+" edited" });
        Assert.Equal(ids.Length,edited["changed"]!.GetValue<int>());
        Assert.Single(edited["notices"]!.AsArray(),n=>n!["message"]!.GetValue<string>()==text+" edited");
        await StudioRefusedAsync(new { action="notice_edit",ids,message="" });
        await StudioRefusedAsync(new { action="notice_delete",ids=Array.Empty<long>() });

        JsonObject deleted=await AdminPathAsync("/admin/accounts",new { action="notice_delete",ids });
        Assert.DoesNotContain(deleted["notices"]!.AsArray(),n=>n!["message"]!.GetValue<string>().StartsWith(text,StringComparison.Ordinal));
        await StudioRefusedAsync(new { action="notice_delete",ids });
    }

    [Fact]
    public async Task Studio_event_controls_change_actual_entry_and_refuse_invalid_schedules()
    {
        long user=await StudioUserAsync("event");
        await AdminPathAsync("/admin/accounts",new { action="event_open",stage_id=3250200,seconds=300 });
        JsonObject open=await AdminPathAsync("/admin/accounts?events=1",null,HttpMethod.Get);
        Assert.True(open["events"]!.AsArray().Single(e=>e!["StageId"]!.GetValue<long>()==3250200)!["Open"]!.GetValue<bool>());
        Assert.Equal(0,(await CallAsync(71,new { stage_id=3250200,stage_mode=11,friend_type=0,friend_user_no=0 },user))["errno"]!.GetValue<long>());
        // The public calendar the browser writes over the client's stage table: whole hours, covering now.
        using (HttpResponseMessage response=await _client.GetAsync("/api/events"))
        {
            response.EnsureSuccessStatusCode();
            JsonArray row=JsonNode.Parse(await response.Content.ReadAsStringAsync())!["events"]!.AsArray()
                .Single(e=>e![0]!.GetValue<long>()==3250200)!.AsArray();
            long start=row[1]!.GetValue<long>(),end=row[2]!.GetValue<long>(),now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Assert.True(start%3600==0&&end%3600==0&&start<=now&&end>=now+300&&end-start<=3*3600);
            Assert.Equal(end-start,row[3]!.GetValue<long>());
        }
        await StudioRefusedAsync(new { action="event_schedule",stage_id=3250200,start_time=100,end_time=50,duration_seconds=10,period_seconds=100 });
        await StudioRefusedAsync(new { action="event_schedule",stage_id=3250200,start_time=0,end_time=2140000000,duration_seconds=86400,period_seconds=86400 });
        await StudioRefusedAsync(new { action="event_open",stage_id=3250201,seconds=300 });
        JsonObject unchanged=await AdminPathAsync("/admin/accounts?events=1",null,HttpMethod.Get);
        Assert.Equal(open["events"]!.ToJsonString(),unchanged["events"]!.ToJsonString());
        await AdminPathAsync("/admin/accounts",new { action="event_close",stage_id=3250200 });
        Assert.NotEqual(0,(await CallAsync(71,new { stage_id=3250200,stage_mode=11,friend_type=0,friend_user_no=0 },user))["errno"]!.GetValue<long>());
        await AdminPathAsync("/admin/accounts",new { action="event_schedule",stage_id=3250200,start_time=0,end_time=2139998400,duration_seconds=86400,period_seconds=86400 });
        Assert.True((await AdminPathAsync("/admin/accounts?events=1",null,HttpMethod.Get))["events"]!.AsArray().Single(e=>e!["StageId"]!.GetValue<long>()==3250200)!["Open"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Studio_opens_every_event_dungeon_and_restores_the_regular_schedule()
    {
        await AdminPathAsync("/admin/accounts",new { action="event_schedule",stage_id=3250200,start_time=0,end_time=2139998400,duration_seconds=86400,period_seconds=86400 });
        JsonObject before=await AdminPathAsync("/admin/accounts?events=1",null,HttpMethod.Get);
        JsonNode regular=before["events"]!.AsArray().Single(e=>e!["StageId"]!.GetValue<long>()==3250200)!;
        Assert.False(before["all_open"]!.GetValue<bool>());

        JsonObject all=await AdminPathAsync("/admin/accounts",new { action="event_open_all" });
        Assert.True(all["all_open"]!.GetValue<bool>());
        Assert.All(all["events"]!.AsArray(),e=>Assert.Equal(e!["Playable"]!.GetValue<bool>(),e["Open"]!.GetValue<bool>()));
        Assert.Equal(67,before["events"]!.AsArray().Count);
        Assert.Equal(40,all["events"]!.AsArray().Count);
        Assert.Equal(35,all["events"]!.AsArray().Count(e=>e!["Open"]!.GetValue<bool>()));
        Assert.All(all["events"]!.AsArray().Where(e=>!e!["Playable"]!.GetValue<bool>()),
            e=>Assert.Contains("maps are missing",e!["UnavailableReason"]!.GetValue<string>()));
        await StudioRefusedAsync(new { action="event_open",stage_id=3200810,seconds=3600 });
        await StudioRefusedAsync(new { action="event_schedule",stage_id=3201000,start_time=0,end_time=2140002800,duration_seconds=3600,period_seconds=3600 });
        Assert.DoesNotContain(all["events"]!.AsArray(),e=>KnS.Data.Repositories.EventStageRepository.IsOpenAllDuplicate(e!["StageId"]!.GetValue<long>()));
        // Franken's Lab and Pit of Despair remain distinct dungeons. Table 17 labels the former
        // Ghost Ship, but DrawEvent reads table 6; don't merge the language tables for identity.
        Assert.Contains(all["events"]!.AsArray(),e=>e!["StageId"]!.GetValue<long>()==3200220);
        Assert.Contains(all["events"]!.AsArray(),e=>e!["StageId"]!.GetValue<long>()==3200280);
        // Pressing it again keeps the first saved calendar.
        await AdminPathAsync("/admin/accounts",new { action="event_open_all" });

        // An already-open database from the old implementation heals on read, without another click.
        using (var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(_folder,"kns.sqlite")))
        {
            connection.Open();
            using var command=connection.CreateCommand();
            command.CommandText="UPDATE master_event_schedule SET end_time=start_time+31536000";
            command.ExecuteNonQuery();
        }
        using (HttpResponseMessage response=await _client.GetAsync("/api/events"))
        {
            response.EnsureSuccessStatusCode();
            JsonArray calendar=JsonNode.Parse(await response.Content.ReadAsStringAsync())!["events"]!.AsArray();
            Assert.Equal(35,calendar.Count(e=>e![2]!.GetValue<long>()>e[1]!.GetValue<long>()));
            Assert.All(calendar.Where(e=>KnS.Data.Repositories.EventStageRepository.IsOpenAllDuplicate(e![0]!.GetValue<long>())),
                e=>Assert.Equal(e![1]!.GetValue<long>(),e[2]!.GetValue<long>()));
        }
        long user=await StudioUserAsync("event-no-duplicates");
        Assert.NotEqual(0,(await CallAsync(71,new { stage_id=3200710,stage_mode=11,friend_type=0,friend_user_no=0 },user))["errno"]!.GetValue<long>());

        JsonObject restored=await AdminPathAsync("/admin/accounts",new { action="event_restore_all" });
        Assert.False(restored["all_open"]!.GetValue<bool>());
        JsonNode after=restored["events"]!.AsArray().Single(e=>e!["StageId"]!.GetValue<long>()==3250200)!;
        Assert.Equal(regular.ToJsonString(),after.ToJsonString());
        Assert.Equal(before["events"]!.ToJsonString(),restored["events"]!.ToJsonString());
        Assert.Contains(restored["events"]!.AsArray(),e=>!e!["Open"]!.GetValue<bool>());
        await AdminPathAsync("/admin/accounts",new { action="event_restore_all" });
    }

    private async Task<long> StudioUserAsync(string name)
    {
        await AdminAsync("",new { email=name+"@studio.example" });
        await AdminAsync("attach",new { email=name+"@studio.example",device_id="studio-"+name });
        return (await CallAsync(51,new { hub_did="studio-"+name }))["user_no"]!.GetValue<long>();
    }

    [Fact]
    public async Task Studio_rarity_options_validate_ranges_and_bulk_failure_rolls_back()
    {
        long user=await StudioUserAsync("options");
        await AdminPathAsync("/admin/accounts",new { action="item",user_no=user,item_id=200001,option_mode="random",option_count=3 });
        var snapshot=await StudioSnapshotAsync(user);
        long number=snapshot["items"]!.AsArray().Single(x=>x!["ItemId"]!.GetValue<long>()==200001)!["ItemNumber"]!.GetValue<long>();
        Assert.Equal(3,snapshot["item_options"]![number.ToString(System.Globalization.CultureInfo.InvariantCulture)]!.AsArray().Count);
        await AdminPathAsync("/admin/accounts",new { action="item_edit",user_no=user,number,locked=0,option_mode="manual",options=new[] { new { id=2020401,value1=3,value2=0 } } });
        var manual=await StudioSnapshotAsync(user);
        var options=manual["item_options"]![number.ToString(System.Globalization.CultureInfo.InvariantCulture)]!.AsArray();
        Assert.Single(options); Assert.Equal(3,options[0]!["Value1"]!.GetValue<long>());
        await StudioRefusedAsync(new { action="item_edit",user_no=user,number,locked=0,option_mode="manual",options=new[] { new { id=2020401,value1=999,value2=0 } } });
        await StudioRefusedAsync(new { action="item",user_no=user,item_id=200001,option_mode="random",option_count=6 });
        Assert.Equal(manual["item_options"]!.ToJsonString(),(await StudioSnapshotAsync(user))["item_options"]!.ToJsonString());
        await StudioRefusedAsync(new { action="inventory_batch",user_no=user,entries=new object[] { new { action="fellow",item_id=100001 },new { action="item",item_id=999999 } } });
        Assert.Equal(manual["fellows"]!.ToJsonString(),(await StudioSnapshotAsync(user))["fellows"]!.ToJsonString());
        await AdminPathAsync("/admin/accounts",new { action="inventory_batch",user_no=user,entries=new object[] { new { action="item",item_id=200011,option_mode="random",option_count=0 },new { action="fellow",item_id=100001 } } });
        await AdminPathAsync("/admin/accounts",new { action="item_edit",user_no=user,number,locked=1 });
        var bulk=await StudioSnapshotAsync(user);
        long other=bulk["items"]!.AsArray().Single(x=>x!["ItemId"]!.GetValue<long>()==200011)!["ItemNumber"]!.GetValue<long>();
        await StudioRefusedAsync(new { action="inventory_batch",user_no=user,entries=new[] { new { action="item_delete",number=other },new { action="item_delete",number } } });
        Assert.Equal(bulk["items"]!.ToJsonString(),(await StudioSnapshotAsync(user))["items"]!.ToJsonString());
        await AdminPathAsync("/admin/accounts",new { action="inventory_batch",user_no=user,entries=new[] { new { action="item_delete",number=other } } });
        Assert.DoesNotContain((await StudioSnapshotAsync(user))["items"]!.AsArray(),x=>x!["ItemNumber"]!.GetValue<long>()==other);
    }

    [Fact]
    public async Task Studio_party_swaps_preserve_both_allies_and_refuse_stale_busy_or_foreign_edits()
    {
        long user=await StudioUserAsync("swap"), other=await StudioUserAsync("swapother");
        await AdminPathAsync("/admin/accounts",new { action="fellow",user_no=user,item_id=100001,quantity=3 });
        var initial=await StudioSnapshotAsync(user);
        var numbers=initial["fellows"]!.AsArray().Where(x=>x!["FellowId"]!.GetValue<long>()==100001).Select(x=>x!["FellowNumber"]!.GetValue<long>()).ToArray();
        long first=numbers[0], second=numbers[1], reserve=numbers[2];
        await AdminPathAsync("/admin/accounts",new { action="fellow_slot",user_no=user,number=first,setup=3,position=0 });
        await AdminPathAsync("/admin/accounts",new { action="fellow_slot",user_no=user,number=second,setup=3,position=1 });
        await AdminPathAsync("/admin/accounts",new { action="fellow_swap",user_no=user,number=first,setup=3,position=1,expected_target=second });
        var swapped=await StudioSnapshotAsync(user);
        Assert.Equal(second,PartyAt(swapped,0));Assert.Equal(first,PartyAt(swapped,1));
        await AdminPathAsync("/admin/accounts",new { action="fellow_swap",user_no=user,number=second,setup=3,position=2,expected_target=0 });
        var moved=await StudioSnapshotAsync(user);
        Assert.Equal(0,PartyAt(moved,0));Assert.Equal(second,PartyAt(moved,2));
        await AdminPathAsync("/admin/accounts",new { action="fellow_swap",user_no=user,number=reserve,setup=3,position=1,expected_target=first });
        var replaced=await StudioSnapshotAsync(user);
        Assert.Equal(reserve,PartyAt(replaced,1));Assert.DoesNotContain(replaced["party"]!.AsArray(),x=>x!["SetupType"]!.GetValue<long>()==3&&x["FellowNumber"]!.GetValue<long>()==first);
        await StudioRefusedAsync(new { action="fellow_swap",user_no=user,number=first,setup=3,position=1,expected_target=first });
        await StudioRefusedAsync(new { action="fellow_swap",user_no=other,number=reserve,setup=3,position=0,expected_target=0 });
        StudioSql($"INSERT INTO fellow_mission(user_no,mission_id,status,fellows,soldiers,end_time) VALUES({user},6200001,1,'{reserve}','',0)");
        await StudioRefusedAsync(new { action="fellow_swap",user_no=user,number=second,setup=3,position=1,expected_target=reserve });
        await StudioRefusedAsync(new { action="fellow_swap",user_no=user,number=reserve,setup=3,position=0,expected_target=0 });
        Assert.Equal(replaced["party"]!.ToJsonString(),(await StudioSnapshotAsync(user))["party"]!.ToJsonString());
    }

    private static long PartyAt(JsonObject snapshot, long position) => snapshot["party"]!.AsArray()
        .FirstOrDefault(x=>x!["SetupType"]!.GetValue<long>()==3&&x["Position"]!.GetValue<long>()==position)?["FellowNumber"]?.GetValue<long>()??0;

    private Task<JsonObject> StudioSnapshotAsync(long user) => AdminPathAsync($"/admin/accounts?user_no={user}",null,HttpMethod.Get);

    private async Task StudioRefusedAsync(object body)
    {
        using var message=new HttpRequestMessage(HttpMethod.Post,"/admin/accounts");
        message.Headers.Add("X-Admin-Key","secret");
        message.Content=new StringContent(WireJson.ToJsonObject(body).ToJsonString(),Encoding.UTF8,"application/json");
        using HttpResponseMessage response=await _client.SendAsync(message);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,response.StatusCode);
    }

    private void StudioSql(string sql)
    {
        using var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(_folder,"kns.sqlite"));
        connection.Open();using var command=connection.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Raid_editor_controls_global_schedule_and_preserves_old_attempts()
    {
        using var unauthorized = new HttpRequestMessage(HttpMethod.Get, "/admin/accounts?raid=1");
        using HttpResponseMessage rejected = await _client.SendAsync(unauthorized);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);
        await AdminAsync("", new { email = "raid-editor@example.com" });
        await AdminAsync("attach", new { email = "raid-editor@example.com", device_id = "raid-editor-device" });
        long user = (await CallAsync(51, new { hub_did = "raid-editor-device" }))["user_no"]!.GetValue<long>();
        JsonObject before = await AdminPathAsync("/admin/accounts?raid=1", null, HttpMethod.Get);
        long oldRaid = before["raid"]!["raid_no"]!.GetValue<long>();
        await CallAsync(503, new { raid_no = oldRaid }, user);
        JsonObject timer = await AdminPathAsync("/admin/accounts", new { action = "raid_time", seconds = 3600 });
        Assert.Equal(oldRaid, timer["raid"]!["raid_no"]!.GetValue<long>());
        Assert.InRange(timer["raid"]!["remain_time"]!.GetValue<long>(), 3598, 3600);
        JsonObject next = await AdminPathAsync("/admin/accounts", new { action = "raid_next" });
        long newRaid = next["raid"]!["raid_no"]!.GetValue<long>();
        Assert.True(newRaid > oldRaid);
        JsonObject board = await CallAsync(501, new { }, user);
        Assert.Equal(newRaid, board["ongoing_raid_info"]![0]!["raid_no"]!.GetValue<long>());
        JsonObject result = await CallAsync(504, new { score = 1234 }, user);
        Assert.Equal(oldRaid, result["user_raid_info"]!["raid_no"]!.GetValue<long>());
        Assert.Equal(1234, result["user_raid_info"]!["score"]!.GetValue<long>());
        JsonObject selected = await AdminPathAsync("/admin/accounts", new { action = "raid_boss", stage_id = 3100015 });
        Assert.Equal(3100015, selected["raid"]!["stage_id"]!.GetValue<long>());
        Assert.True(selected["raid"]!["raid_no"]!.GetValue<long>() > newRaid);
        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/admin/accounts");
        invalid.Headers.Add("X-Admin-Key", "secret");
        invalid.Content = new StringContent("{\"action\":\"raid_time\",\"seconds\":0}", Encoding.UTF8, "application/json");
        using HttpResponseMessage bad = await _client.SendAsync(invalid);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(_folder, "kns.sqlite"));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE raid_control SET ends_at=0 WHERE id=1";
        command.ExecuteNonQuery();
        // Set a recent expiration to test automatic advancement without skipping thousands of weeks.
        command.CommandText = "UPDATE raid_control SET ends_at=" + (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1) + " WHERE id=1";
        command.ExecuteNonQuery();
        JsonObject rolled = await AdminPathAsync("/admin/accounts?raid=1", null, HttpMethod.Get);
        Assert.Equal(selected["raid"]!["raid_no"]!.GetValue<long>() + 1, rolled["raid"]!["raid_no"]!.GetValue<long>());
    }

    [Fact]
    public async Task Arena_editor_persists_season_one_and_archives_rollover_without_losing_attempts()
    {
        using var unauthorized = new HttpRequestMessage(HttpMethod.Get, "/admin/accounts?arena=1");
        using HttpResponseMessage rejected = await _client.SendAsync(unauthorized);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);
        await AdminAsync("", new { email = "arena-editor@example.com" });
        await AdminAsync("attach", new { email = "arena-editor@example.com", device_id = "arena-editor-device" });
        await AdminAsync("", new { email = "arena-opponent@example.com" });
        await AdminAsync("attach", new { email = "arena-opponent@example.com", device_id = "arena-opponent-device" });
        long user = (await CallAsync(51, new { hub_did = "arena-editor-device" }))["user_no"]!.GetValue<long>();
        long opponent = (await CallAsync(51, new { hub_did = "arena-opponent-device" }))["user_no"]!.GetValue<long>();
        await CallAsync(60, new { }, user);
        await CallAsync(60, new { }, opponent);
        JsonObject first = await AdminPathAsync("/admin/accounts?arena=1", null, HttpMethod.Get);
        Assert.Equal(1, first["arena"]!["season"]!.GetValue<long>());
        Assert.Equal(1, (await CallAsync(405, new { }, user))["week"]!.GetValue<long>());
        await CallAsync(402, new { pvp_user_no = opponent }, user);
        var result = new { pvp_user_no = opponent, give_up = 0, pvp_player_hp = 100, pvp_player_max_hp = 100, pvp_enemy_hp = 0, pvp_enemy_max_hp = 100 };
        await CallAsync(403, result, user);
        await CallAsync(402, new { pvp_user_no = opponent }, user);
        JsonObject renamed = await AdminPathAsync("/admin/accounts", new { action = "arena_number", season = 7 });
        Assert.Equal(7, renamed["arena"]!["season"]!.GetValue<long>());
        Assert.Equal(14, (await CallAsync(408, new { }, user))["user_pvp_ranking_info"]!["user_pvp_point"]!.GetValue<long>());
        JsonObject timer = await AdminPathAsync("/admin/accounts", new { action = "arena_time", seconds = 3600 });
        Assert.InRange(timer["arena"]!["remain_time"]!.GetValue<long>(), 3598, 3600);
        JsonObject ended = await AdminPathAsync("/admin/accounts", new { action = "arena_end" });
        Assert.True(ended["arena"]!["closed"]!.GetValue<bool>());
        Assert.NotEqual(0, (await CallAsync(402, new { pvp_user_no = user }, opponent))["errno"]!.GetValue<long>());
        JsonObject next = await AdminPathAsync("/admin/accounts", new { action = "arena_next" });
        Assert.Equal(8, next["arena"]!["season"]!.GetValue<long>());
        Assert.Equal(0, (await CallAsync(403, result, user))["user_pvp_ranking_info"]!["user_pvp_point"]!.GetValue<long>());
        await CallAsync(403, result, user);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(_folder, "kns.sqlite"));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT points FROM pvp_history WHERE generation=1 AND user_no=" + user;
        Assert.Equal(28L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        command.CommandText = "SELECT count FROM user_goods WHERE item_id=1900017 AND user_no=" + user;
        Assert.Equal(8L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        command.CommandText = "UPDATE pvp_control SET ends_at=" + (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1) + " WHERE id=1";
        command.ExecuteNonQuery();
        JsonObject automatic = await AdminPathAsync("/admin/accounts?arena=1", null, HttpMethod.Get);
        Assert.Equal(9, automatic["arena"]!["season"]!.GetValue<long>());
        // Season ranking rewards: season 7 (generation 1, Leon pair) had one fighter, ranked 1st -> 6★ Holy Paladin Leon (105017) + 500 Gems,
        // mailed once under "PvP Reward". Season 8 had no battles, so the automatic rollover pays nothing; the opponent never fought, so gets nothing.
        command.CommandText = "SELECT COUNT(*) FROM reward_mail WHERE text_code=9800021 AND user_no=" + user;
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        command.CommandText = "SELECT item_id1 || ',' || value1 || ',' || item_id2 || ',' || value2 FROM reward_mail WHERE text_code=9800021 AND user_no=" + user;
        Assert.Equal("105017,1,1900009,500", command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM reward_mail WHERE text_code=9800021 AND user_no=" + opponent;
        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        JsonObject claimed = (await CallAsync(404, new { }, user))["reward_pvp_info"]!.AsObject();
        Assert.Equal(1, claimed["Grade"]!.GetValue<long>());
        Assert.Equal(105017, claimed["Reward1_Item"]!.GetValue<long>());
        Assert.Equal(500, claimed["Reward2_Value"]!.GetValue<long>());
        Assert.Empty((await CallAsync(404, new { }, opponent))["reward_pvp_info"]!.AsObject());

        // Studio featured ally override: pin Succubus Queen Lyrith (100512), verify 405 reflects it, reject invalid id,
        // settle with 4 fighters (ranks 1-3 get 6★ 100512, rank 4 gets 5★ 100511), reset pin to auto on next season,
        // and verify 404 keeps the settled ally while 115 delivers it into fellow + fellow_book.
        JsonObject arenaMeta = await AdminPathAsync("/admin/accounts?arena=1", null, HttpMethod.Get);
        Assert.Equal(7, arenaMeta["arena_allies"]!.AsArray().Count);
        JsonObject pinned = await AdminPathAsync("/admin/accounts", new { action = "arena_ally", featured_ally = 100512 });
        Assert.Equal(100512, pinned["arena"]!["featured_ally"]!.GetValue<long>());
        Assert.Equal(100512, pinned["arena"]!["featured_ally_override"]!.GetValue<long>());
        JsonArray pinnedRewards = (await CallAsync(405, new { }, user))["pvp_ranking_reward_info"]!.AsArray();
        Assert.Equal(100512, pinnedRewards[0]!["Reward1_Item"]!.GetValue<long>());
        Assert.Equal(100512, pinnedRewards[2]!["Reward1_Item"]!.GetValue<long>());
        Assert.Equal(100511, pinnedRewards[3]!["Reward1_Item"]!.GetValue<long>());
        Assert.Equal(100511, pinnedRewards[7]!["Reward1_Item"]!.GetValue<long>());
        Assert.Equal(1900012, pinnedRewards[8]!["Reward1_Item"]!.GetValue<long>());

        using var badAlly = new HttpRequestMessage(HttpMethod.Post, "/admin/accounts");
        badAlly.Headers.Add("X-Admin-Key", "secret");
        badAlly.Content = new StringContent("{\"action\":\"arena_ally\",\"featured_ally\":100001}", Encoding.UTF8, "application/json");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await _client.SendAsync(badAlly)).StatusCode);

        await AdminAsync("", new { email = "arena-r3@example.com" });
        await AdminAsync("attach", new { email = "arena-r3@example.com", device_id = "arena-r3-device" });
        await AdminAsync("", new { email = "arena-r4@example.com" });
        await AdminAsync("attach", new { email = "arena-r4@example.com", device_id = "arena-r4-device" });
        long r3 = (await CallAsync(51, new { hub_did = "arena-r3-device" }))["user_no"]!.GetValue<long>();
        long r4 = (await CallAsync(51, new { hub_did = "arena-r4-device" }))["user_no"]!.GetValue<long>();
        await CallAsync(60, new { }, r3);
        await CallAsync(60, new { }, r4);
        command.CommandText = $"INSERT OR REPLACE INTO pvp_stat(user_no,points) VALUES({user},400),({opponent},300),({r3},200),({r4},100)";
        command.ExecuteNonQuery();

        JsonObject afterPinned = await AdminPathAsync("/admin/accounts", new { action = "arena_next" });
        Assert.Equal(10, afterPinned["arena"]!["season"]!.GetValue<long>());
        Assert.Equal(0, afterPinned["arena"]!["featured_ally_override"]!.GetValue<long>());
        // Season 10 auto-cycles to index (10 - 1) % 7 = 2 -> Lyrith (100512), and Season 9's payouts used pinned Lyrith too.
        command.CommandText = $"SELECT item_id1 FROM reward_mail WHERE text_code=9800021 AND user_no={r3} ORDER BY rowid_alias DESC LIMIT 1";
        Assert.Equal(100512L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        command.CommandText = $"SELECT item_id1 FROM reward_mail WHERE text_code=9800021 AND user_no={r4} ORDER BY rowid_alias DESC LIMIT 1";
        Assert.Equal(100511L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(100511, (await CallAsync(404, new { }, r4))["reward_pvp_info"]!["Reward1_Item"]!.GetValue<long>());
        command.CommandText = $"SELECT rowid_alias FROM reward_mail WHERE text_code=9800021 AND user_no={r4} ORDER BY rowid_alias DESC LIMIT 1";
        long r4Mail = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        JsonObject r4Claim = await CallAsync(115, new { mail_list = new[] { r4Mail } }, r4);
        Assert.Equal(0, r4Claim["errno"]!.GetValue<long>());
        Assert.Equal(100511, Assert.Single(r4Claim["fellow_info"]!.AsArray())!["fellow_id"]!.GetValue<long>());
        command.CommandText = $"SELECT COUNT(*) FROM fellow_book WHERE user_no={r4} AND fellow_id=100511";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));

        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/admin/accounts");
        invalid.Headers.Add("X-Admin-Key", "secret");
        invalid.Content = new StringContent("{\"action\":\"arena_number\",\"season\":0}", Encoding.UTF8, "application/json");
        using HttpResponseMessage bad = await _client.SendAsync(invalid);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(10, (await AdminPathAsync("/admin/accounts?arena=1", null, HttpMethod.Get))["arena"]!["season"]!.GetValue<long>());
    }

    [Fact]
    public async Task Studio_mail_all_sends_multi_item_mail_with_title_and_description_to_all_players()
    {
        await AdminAsync("", new { email = "mail-all-1@example.com" });
        await AdminAsync("attach", new { email = "mail-all-1@example.com", device_id = "mail-all-1" });
        long p1 = (await CallAsync(51, new { hub_did = "mail-all-1" }))["user_no"]!.GetValue<long>();
        await CallAsync(60, new { }, p1);

        await AdminAsync("", new { email = "mail-all-2@example.com" });
        await AdminAsync("attach", new { email = "mail-all-2@example.com", device_id = "mail-all-2" });
        long p2 = (await CallAsync(51, new { hub_did = "mail-all-2" }))["user_no"]!.GetValue<long>();
        await CallAsync(60, new { }, p2);

        JsonObject sent = await AdminPathAsync("/admin/accounts", new
        {
            action = "mail_all",
            title = "Weekend Gift",
            message = "Enjoy stamina, gold, and an ally!",
            items = new object[]
            {
                new { item_id = 1900014, count = 50 },
                new { item_id = 1900008, count = 1000 },
                new { item_id = 110004, count = 1 },
            },
        });

        Assert.True(sent["ok"]!.GetValue<bool>());
        Assert.True(sent["players"]!.GetValue<long>() >= 2);
        Assert.Equal(2, sent["mails_per_player"]!.GetValue<long>());

        foreach (long user in new[] { p1, p2 })
        {
            JsonObject inbox = await CallAsync(63, new { type = 2, page = 1, count = 30 }, user);
            JsonArray list = inbox["mail_info"]!.AsArray();
            Assert.True(list.Count >= 2);
            JsonObject newest = list[0]!.AsObject();
            Assert.Equal("Weekend Gift", newest["send_user_hub_id"]!.GetValue<string>());
            Assert.Equal("Weekend Gift\nEnjoy stamina, gold, and an ally!", newest["message"]!.GetValue<string>());
            Assert.Equal(0, newest["text_code"]!.GetValue<long>());

            long[] mailIds = list.Take(2).Select(m => m!["mail_no"]!.GetValue<long>()).ToArray();
            JsonObject claimed = await CallAsync(115, new { mail_list = mailIds }, user);
            Assert.Equal(0, claimed["errno"]!.GetValue<long>());
            Assert.Single(claimed["fellow_info"]!.AsArray());
        }
    }

    [Fact]
    public async Task The_pre_login_calls_need_no_identity()
    {
        Assert.Equal(0, (await CallAsync(48, new { }))["errno"]!.GetValue<int>());
        Assert.Equal(0, (await CallAsync(50, new { strBundleIdentifier = "x" }))["errno"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_rebuild_clients_whole_login_sequence_passes_for_a_tester_and_stops_at_51_for_others()
    {
        await AdminAsync("", new { email = "seq@example.com" });
        await AdminAsync("attach", new { email = "seq@example.com", device_id = "seq-device" });

        Assert.Equal(0, (await CallAsync(48, new { }))["errno"]!.GetValue<int>());
        Assert.Equal(0, (await CallAsync(50, new { }))["errno"]!.GetValue<int>());
        long user = (await CallAsync(51, new { hub_did = "seq-device" }))["user_no"]!.GetValue<long>();
        foreach (int protocol in new[] { 60, 61, 330, 601, 141, 501, 140, 59 })
        {
            Assert.Equal(0, (await CallAsync(protocol, new { user_no = user }, user))["errno"]!.GetValue<int>());
        }

        Assert.Equal(1, (await CallAsync(51, new { hub_did = "someone-else" }))["errno"]!.GetValue<int>());
    }

    private async Task<JsonObject> AdminPathAsync(string path, object? body, HttpMethod? method = null)
    {
        using var message = new HttpRequestMessage(method ?? HttpMethod.Post, path);
        if (body is not null)
        {
            message.Content = new StringContent(WireJson.ToJsonObject(body).ToJsonString(), Encoding.UTF8, "application/json");
        }

        message.Headers.Add("X-Admin-Key", "secret");
        using HttpResponseMessage response = await _client.SendAsync(message);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private async Task<JsonObject> AdminAsync(string action, object? body, HttpMethod? method = null)
    {
        using HttpResponseMessage response = await SendAdminAsync(action, body, "secret", method);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private Task<HttpResponseMessage> SendAdminAsync(string action, object? body, string? key, HttpMethod? method = null)
    {
        var message = new HttpRequestMessage(method ?? HttpMethod.Post, "/admin/testers" + (action.Length > 0 ? "/" + action : ""));
        if (body is not null)
        {
            message.Content = new StringContent(WireJson.ToJsonObject(body).ToJsonString(), Encoding.UTF8, "application/json");
        }

        if (key is not null)
        {
            message.Headers.Add("X-Admin-Key", key);
        }

        return _client.SendAsync(message);
    }

    private readonly Dictionary<long, string> _sessionKeys = [];

    private async Task<JsonObject> CallAsync(int protocol, object parameters, long userNumber = 0)
    {
        var request = new JsonObject { ["params"] = WireJson.ToJsonObject(parameters), ["protocol"] = protocol };
        using var message = new HttpRequestMessage(HttpMethod.Post, "/1mhero/gateway.php")
        {
            Content = new ByteArrayContent(GameGatewayCipher.Encrypt(Encoding.UTF8.GetBytes(request.ToJsonString()))),
        };
        if (userNumber != 0)
        {
            message.Headers.Add("USERNO", userNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (_sessionKeys.TryGetValue(userNumber, out string? sessionKey))
            {
                message.Headers.Add("SESSIONKEY", sessionKey);
            }
        }

        using HttpResponseMessage response = await _client.SendAsync(message);
        JsonObject reply = WireJson.ParseObject(GameGatewayCipher.Decrypt(await response.Content.ReadAsByteArrayAsync()))!;
        if (protocol == 51 && reply["session_key"] is { } key)
        {
            _sessionKeys[reply["user_no"]!.GetValue<long>()] = key.GetValue<string>();
        }

        return reply;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}

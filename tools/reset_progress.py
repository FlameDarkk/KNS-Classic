"""Reset every player's game progress to a brand-new account, keeping accounts, logins and usernames.

    python tools/reset_progress.py --db KNS.sqlite                                  # dry run: prints what would change
    python tools/reset_progress.py --db KNS.sqlite --apply --backup BACKUP.sqlite   # backs up, then resets

Kept: account / login_account (logins), device_link, tester, access_attempt, feature_flag, the arena and raid
schedules (pvp_control, raid_control) and each gameuser row's
identity (user_no, device, hub uid, nickname, created). Every gameuser row goes back to the fresh start (0 gold, 0 gems, level 1,
hero 1, no bought bag slots); every other table with a player column is emptied: allies, items, heroes, story, stages, quests,
missions, arena, raid, friends, mail, stamina, buffs, login rewards, sessions. Master tables are untouched.
Sets server_state progress_epoch, so browser and Android clients drop their local game save (tutorial) once.

A table that is in neither list and has no player column stops the run: decide for it here first. Run with the KnS server
stopped. Everything happens in one transaction after an online backup (sqlite3 backup API) to --backup.
"""
import argparse
import json
import sqlite3
import sys
import time

KEEP = {
    "account", "account_session", "login_account", "device_link", "tester", "access_attempt",
    "feature_flag", "pvp_control", "raid_control", "event_schedule_backup", "chat_pin",
    "server_state", "gameuser",
}
# Global tables without a player column that are progress all the same.
WIPE_GLOBAL = {"raid_rank_settlement", "chat_history"}
PLAYER_COLUMNS = {"user_no", "friend_no", "opponent_no"}
FRESH = {"gold": 0, "cash": 0, "hero_pcid": 1, "level": 1, "exp": 0, "inventory_cash": 0, "fellow_inventory_cash": 0, "fellow_item_inventory_cash": 0}


def tables(db):
    names = [row[0] for row in db.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'master_%' ORDER BY name")]
    return {name: [row[1] for row in db.execute(f'PRAGMA table_info("{name}")')] for name in names}


def plan(db):
    schema = tables(db)
    wipe, unknown = [], []
    for name, columns in schema.items():
        if name in KEEP:
            continue
        if name in WIPE_GLOBAL or PLAYER_COLUMNS & set(columns):
            wipe.append(name)
        else:
            unknown.append(name)
    missing = [column for column in FRESH if column not in schema.get("gameuser", [])]
    counts = {name: db.execute(f'SELECT COUNT(*) FROM "{name}"').fetchone()[0] for name in wipe}
    players = db.execute("SELECT COUNT(*) FROM gameuser").fetchone()[0]
    return {"players": players, "wipe": counts, "keep": sorted(KEEP & schema.keys()), "unknown": unknown, "missing_columns": missing}


def apply(db, report, epoch):
    db.execute("BEGIN EXCLUSIVE")
    try:
        for name in report["wipe"]:
            db.execute(f'DELETE FROM "{name}"')
        db.execute("UPDATE gameuser SET " + ", ".join(f"{column}=?" for column in FRESH), list(FRESH.values()))
        db.execute("CREATE TABLE IF NOT EXISTS server_state(state_key TEXT PRIMARY KEY, value INTEGER)")
        db.execute("INSERT OR REPLACE INTO server_state(state_key, value) VALUES('progress_epoch', ?)", (epoch,))
        db.execute("COMMIT")
    except Exception:
        db.execute("ROLLBACK")
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--db", required=True, help="the KnS SQLite database")
    parser.add_argument("--apply", action="store_true", help="reset (default: dry run)")
    parser.add_argument("--backup", help="where to write the backup before resetting (required with --apply)")
    args = parser.parse_args()

    db = sqlite3.connect(args.db, timeout=30, isolation_level=None)
    report = plan(db)
    if report["unknown"] or report["missing_columns"]:
        print(json.dumps(report, indent=2))
        sys.exit("Stopped: classify the unknown tables (KEEP / WIPE_GLOBAL) or check gameuser's columns first.")
    if not args.apply:
        print(json.dumps({**report, "dry_run": True}, indent=2))
        return
    if not args.backup:
        sys.exit("--apply needs --backup PATH")
    with sqlite3.connect(args.backup) as backup:
        db.backup(backup)
    epoch = int(time.time())
    apply(db, report, epoch)
    left = {name: db.execute(f'SELECT COUNT(*) FROM "{name}"').fetchone()[0] for name in report["wipe"]}
    if any(left.values()):
        sys.exit(f"Reset incomplete: {left}")
    print(json.dumps({**report, "applied": True, "progress_epoch": epoch, "backup": args.backup}, indent=2))


if __name__ == "__main__":
    main()

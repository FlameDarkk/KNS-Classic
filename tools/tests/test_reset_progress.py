"""Regression tests for tools/reset_progress.py against the current server schema."""
import re
import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import reset_progress  # noqa: E402

SCHEMA_CS = ROOT / "server-dotnet" / "src" / "KnS.Data" / "Schema" / "GameSchema.cs"


def _build_schema_db(path: Path) -> sqlite3.Connection:
    """Create a SQLite database containing every table and added column declared in GameSchema.cs."""
    source = SCHEMA_CS.read_text(encoding="utf-8")
    sql_types = {
        "{Sql.AutoIncrementPrimaryKey}": "INTEGER PRIMARY KEY AUTOINCREMENT",
        "{Sql.IntegerColumn}": "INTEGER",
        "{Sql.TextColumn}": "TEXT",
        "{Sql.KeyTextColumn}": "TEXT",
        "{Sql.RealColumn}": "REAL",
    }
    db = sqlite3.connect(path)
    try:
        for match in re.finditer(r'CREATE TABLE IF NOT EXISTS\s+([a-zA-Z0-9_]+)\s*\((.*?)\)\s*"""', source, re.S):
            name, body = match.group(1), match.group(2)
            for token, replacement in sql_types.items():
                body = body.replace(token, replacement)
            db.execute(f"CREATE TABLE IF NOT EXISTS {name} ({body})")

        for match in re.finditer(r'AddMissingIntegerColumns\("([a-zA-Z0-9_]+)",\s*\[(.*?)\]\)', source, re.S):
            table, items = match.group(1), match.group(2)
            for col, default in re.findall(r'\("([a-zA-Z0-9_]+)",\s*([^)]+)\)', items):
                default_val = "0" if not default.strip().lstrip("-").isdigit() else default.strip()
                existing = {row[1] for row in db.execute(f'PRAGMA table_info("{table}")')}
                if col not in existing:
                    db.execute(f'ALTER TABLE "{table}" ADD COLUMN "{col}" INTEGER DEFAULT {default_val}')

        for match in re.finditer(r'AddMissingTextColumns\("([a-zA-Z0-9_]+)",\s*\[(.*?)\]\)', source, re.S):
            table, items = match.group(1), match.group(2)
            for col in re.findall(r'"([a-zA-Z0-9_]+)"', items):
                existing = {row[1] for row in db.execute(f'PRAGMA table_info("{table}")')}
                if col not in existing:
                    db.execute(f'ALTER TABLE "{table}" ADD COLUMN "{col}" TEXT')

        db.commit()
    except Exception:
        db.close()
        raise
    return db


class ResetProgressTests(unittest.TestCase):
    def test_all_schema_tables_are_classified(self):
        with tempfile.TemporaryDirectory() as tmp:
            db = _build_schema_db(Path(tmp) / "kns.sqlite")
            try:
                report = reset_progress.plan(db)
            finally:
                db.close()

        self.assertEqual(report["unknown"], [], f"Unclassified tables in GameSchema.cs: {report['unknown']}")
        self.assertEqual(report["missing_columns"], [])
        self.assertIn("account_session", report["keep"])
        self.assertIn("chat_pin", report["keep"])
        self.assertIn("event_schedule_backup", report["keep"])
        self.assertIn("chat_history", report["wipe"])

    def test_apply_wipes_progress_and_preserves_accounts_and_schedules(self):
        with tempfile.TemporaryDirectory() as tmp:
            db_path = Path(tmp) / "kns.sqlite"
            backup_path = Path(tmp) / "backup.sqlite"
            db = _build_schema_db(db_path)
            db.execute(
                "INSERT INTO gameuser(user_no, hub_did, hub_uid, nickname, created, gold, cash, level, exp)"
                " VALUES(10001, 'web-abc', '906001', 'HeroOne', 1.0, 9999, 500, 42, 1234)"
            )
            db.execute(
                "INSERT INTO login_account(login_id, password_hash, did, guest_uid, nickname, created)"
                " VALUES('hero1', 'pbkdf2', 'web-abc', '906001', 'HeroOne', 1.0)"
            )
            db.execute(
                "INSERT INTO account_session(token_hash, login_id, created, expires)"
                " VALUES('hash1', 'hero1', 1.0, 999999.0)"
            )
            db.execute(
                "INSERT INTO chat_pin(message, start_time, end_time, created_at) VALUES('Welcome', 1, 999999, 1)"
            )
            db.execute(
                "INSERT INTO event_schedule_backup(stage_id, start_time, end_time, duration_seconds, period_seconds)"
                " VALUES(9001, 1, 2, 3, 4)"
            )
            db.execute(
                "INSERT INTO chat_history(chat_group, payload, created_at, pin_id) VALUES(1000, 'cipher', 100, 0)"
            )
            db.execute(
                "INSERT INTO item(user_no, item_id, claimed) VALUES(10001, 200001, 1)"
            )
            db.commit()

            backup = sqlite3.connect(backup_path)
            try:
                db.backup(backup)
            finally:
                backup.close()

            db.isolation_level = None
            report = reset_progress.plan(db)
            reset_progress.apply(db, report, epoch=1700000000)

            row = db.execute("SELECT nickname, gold, cash, level, exp FROM gameuser WHERE user_no=10001").fetchone()
            self.assertEqual(row, ("HeroOne", 0, 0, 1, 0))
            self.assertEqual(db.execute("SELECT COUNT(*) FROM login_account").fetchone()[0], 1)
            self.assertEqual(db.execute("SELECT COUNT(*) FROM account_session").fetchone()[0], 1)
            self.assertEqual(db.execute("SELECT COUNT(*) FROM chat_pin").fetchone()[0], 1)
            self.assertEqual(db.execute("SELECT COUNT(*) FROM event_schedule_backup").fetchone()[0], 1)
            self.assertEqual(db.execute("SELECT COUNT(*) FROM chat_history").fetchone()[0], 0)
            self.assertEqual(db.execute("SELECT COUNT(*) FROM item").fetchone()[0], 0)
            self.assertEqual(
                db.execute("SELECT value FROM server_state WHERE state_key='progress_epoch'").fetchone()[0],
                1700000000,
            )
            db.close()

            # Backup still has the pre-reset data.
            backup = sqlite3.connect(backup_path)
            try:
                self.assertEqual(backup.execute("SELECT gold FROM gameuser WHERE user_no=10001").fetchone()[0], 9999)
                self.assertEqual(backup.execute("SELECT COUNT(*) FROM item").fetchone()[0], 1)
            finally:
                backup.close()


if __name__ == "__main__":
    unittest.main()

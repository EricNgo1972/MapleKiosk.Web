#!/usr/bin/env python3
"""Nightly backup of the site's SQLite database (leads) on phoebus.

Copies /var/www/maplekiosk_www/data/maplekiosk.db with SQLite's online backup API (a consistent copy while
the site keeps writing, WAL included), checks the copy, gzips it to
/var/www/maplekiosk_www/backups/maplekiosk-YYYYMMDD-HHMM.db.gz and keeps the newest KEEP of them.

Installed at /var/www/maplekiosk_www/bin/backup-sitedb.py, run by eric's crontab:
    15 3 * * * /usr/bin/python3 /var/www/maplekiosk_www/bin/backup-sitedb.py >> /var/www/maplekiosk_www/backups/backup.log 2>&1
"""
import datetime
import gzip
import os
import shutil
import sqlite3
import sys

ROOT = "/var/www/maplekiosk_www"
SOURCE = os.path.join(ROOT, "data", "maplekiosk.db")
DEST = os.path.join(ROOT, "backups")
KEEP = 30


def main() -> int:
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M")
    if not os.path.exists(SOURCE):
        print(f"{stamp} FAIL no database at {SOURCE}")
        return 1

    os.makedirs(DEST, exist_ok=True)
    tmp = os.path.join(DEST, f".maplekiosk-{stamp}.db")
    out = os.path.join(DEST, f"maplekiosk-{stamp}.db.gz")
    try:
        src = sqlite3.connect(f"file:{SOURCE}?mode=ro", uri=True)
        dst = sqlite3.connect(tmp)
        with dst:
            src.backup(dst)
        src.close()

        check = dst.execute("PRAGMA integrity_check").fetchone()[0]
        leads = dst.execute("SELECT COUNT(*) FROM leads").fetchone()[0]
        dst.close()
        if check != "ok":
            print(f"{stamp} FAIL integrity_check: {check}")
            return 1

        with open(tmp, "rb") as f, gzip.open(out, "wb") as g:
            shutil.copyfileobj(f, g)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)

    backups = sorted(f for f in os.listdir(DEST) if f.startswith("maplekiosk-") and f.endswith(".db.gz"))
    for old in backups[:-KEEP]:
        os.remove(os.path.join(DEST, old))

    print(f"{stamp} OK {out} ({os.path.getsize(out)} bytes, {leads} leads, {min(len(backups), KEEP)} kept)")
    return 0


if __name__ == "__main__":
    sys.exit(main())

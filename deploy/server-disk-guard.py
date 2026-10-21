#!/usr/bin/env python3
"""Bounded, local host maintenance; emits status to journald, never sends messages."""
import fcntl,json,shutil,subprocess,sys
from pathlib import Path

with Path('/home/ubuntu/maintenance/disk-guard.lock').open('w') as lock:
 try:fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
 except BlockingIOError:sys.exit(0)
 before=shutil.disk_usage('/')
 pressure=(before.total-before.free)/before.total>=0.80 or before.free<30*1024**3
 print(json.dumps({'event':'disk_check','freeGiB':round(before.free/1024**3,2),'usedPercent':round(100*(before.total-before.free)/before.total,1),'cleanupNeeded':pressure}),flush=True)
 if pressure:
  result=subprocess.run(['python3','/home/ubuntu/maintenance/cleanup-builds.py','--apply'])
  if result.returncode:print('Cleanup deferred or failed; inspect the maintenance log.',flush=True)
 after=shutil.disk_usage('/')
 if after.free<20*1024**3 or (after.total-after.free)/after.total>=0.90:
  print('CRITICAL: Disk space remains low after safe cleanup. New deployments must remain blocked; manual inspection required.',flush=True)
  sys.exit(1)
 print(json.dumps({'event':'disk_check_complete','freeGiB':round(after.free/1024**3,2)}),flush=True)

#!/usr/bin/env python3
"""Host build retention. Default is a report; --apply authorizes the bounded cleanup.
Never delete volumes, uploads, database backups, checkout source, or unknown containers.
"""
import concurrent.futures,fcntl,json,os,re,shutil,subprocess,sys,time
from pathlib import Path
os.umask(0o077)
ROOT=Path('/home/ubuntu/releases')
apply='--apply' in sys.argv

def run(*args):return subprocess.check_output(args,text=True)
def inspect(name):return json.loads(run('docker','inspect',name))[0]
def containers():return [json.loads(line) for line in run('docker','ps','-a','--format','json').splitlines()]
def origins(record):
 value=inspect(record['tunnel'])
 result=set()
 for m in value['Mounts']:
  if m['Destination']=='/release.yml':
   for line in Path(m['Source']).read_text().splitlines():
    match=re.match(r'\s*service:\s*https?://([a-zA-Z0-9_.-]+)(?::\d+)?\s*$',line)
    if match:result.add(match.group(1))
 return result

with (ROOT/'bantera-deploy.lock').open('w') as lock:
 fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
 current=json.loads((ROOT/'bantera-current.json').read_text())
 previous=json.loads((Path(current['directory'])/'previous-release.json').read_text())
 assert (Path(current['directory'])/'status').read_text().strip()=='SUCCESS', 'Do not clean an unverified deployment'
 keep={rec[k] for rec in [current,previous] for k in ['api','web','tunnel']}
 pending_dirs=set()
 for release in ROOT.iterdir():
  if not re.fullmatch(r'bantera-20\d{6}-\d+',release.name):continue
  status=(release/'status').read_text().strip() if (release/'status').exists() else ''
  if status in ['', 'BUILDING', 'STAGED', 'PROMOTING'] and time.time()-release.stat().st_mtime < 6*3600:
   pending_dirs.add(release.resolve())
   suffix=release.name.removeprefix('bantera-')
   keep.update(['bantera-api-'+suffix,'bantera-website-'+suffix,'cloudflared-'+suffix])
 for rec in [current,previous]:keep.update(origins(rec))
 all_containers=containers()
 obsolete=[c['Names'] for c in all_containers if (re.fullmatch(r'(bantera-api|bantera-website|cloudflared)-20\d{6}-\d+(?:-[a-z0-9-]+)?',c['Names']) or c['Names'] in ['bantera-api','bantera-website']) and c['Names'] not in keep]
 print('Obsolete Bantera application/connector containers:',len(obsolete),flush=True)
 if apply:
  def remove_container(name):
   if inspect(name)['State']['Running']:subprocess.run(['docker','stop','--time','40',name],stdout=subprocess.DEVNULL,check=True)
   subprocess.run(['docker','rm',name],stdout=subprocess.DEVNULL,check=True)
  with concurrent.futures.ThreadPoolExecutor(max_workers=4) as workers:list(workers.map(remove_container,obsolete))
 # All remaining containers, including other apps and stopped database recovery containers, protect their images and mounts.
 remaining=[c for c in containers() if c['Names'] not in obsolete]
 metadata=[inspect(c['Names']) for c in remaining]
 protected_images={c['Image'] for c in metadata}
 protected_dirs={Path(rec['directory']).resolve() for rec in [current,previous]} | pending_dirs
 for c in metadata:
  for m in c.get('Mounts',[]):
   p=Path(m['Source']).resolve()
   if ROOT in p.parents and len(p.relative_to(ROOT).parts)>0:protected_dirs.add(ROOT/p.relative_to(ROOT).parts[0])
 removed_tags=0
 for row in [json.loads(line) for line in run('docker','image','ls','--format','json').splitlines()]:
  if row['Repository'] not in ['bantera-backend','bantera-api','bantera-website']:continue
  ref=row['Repository']+':'+row['Tag']
  if row['Tag']=='<none>':continue
  image_meta=json.loads(run('docker','image','inspect',ref))[0]
  image_id=image_meta['Id']
  if (image_meta.get('Config',{}).get('Labels') or {}).get('bantera.release') in {p.name for p in pending_dirs}:continue
  if image_id in protected_images:continue
  removed_tags+=1
  if apply:subprocess.run(['docker','image','rm',ref],stdout=subprocess.DEVNULL,check=True)
 reclaimed_sources=0
 for release in ROOT.iterdir():
  if not re.fullmatch(r'bantera-20\d{6}-\d+',release.name) or release.resolve() in protected_dirs:continue
  # Preserve compact manifests, controllers, runtime snapshots and all database backups.
  for name in ['backend','website','backend.tar.gz','website.tar.gz']:
   p=release/name
   if not p.exists() or p.is_symlink():continue
   if p.is_file():reclaimed_sources+=p.stat().st_size
   else:reclaimed_sources+=sum(q.stat().st_size for q in p.rglob('*') if q.is_file() and not q.is_symlink())
   if apply:
    if p.is_dir():
     try:shutil.rmtree(p)
     except PermissionError:
      # Docker-created bin/obj files may be root-owned; this path is a validated obsolete build copy.
      subprocess.run(['sudo','rm','-rf','--',str(p)],check=True)
    else:p.unlink()
 if apply:
  # Cross-application cleanup: only images unused by ANY container, and aged to avoid fresh build work.
  for command in [['docker','image','prune','--force'],['docker','image','prune','--all','--force','--filter','until=72h'],['docker','builder','prune','--all','--force','--keep-storage','2GB']]:
   result=subprocess.run(command,capture_output=True,text=True,check=True)
   print('\n'.join(line for line in result.stdout.splitlines() if 'reclaimed' in line.lower() or line.startswith('Total:')),flush=True)
  processes=run('ps','-eo','args')
  installer_active=any(any(term in line for term in ['npm install','npm ci','pnpm install','pip install','npx ']) for line in processes.splitlines())
  if not installer_active:
   for cache in [Path('/home/ubuntu/.npm/_cacache'),Path('/home/ubuntu/.cache/pip'),Path('/home/ubuntu/.cache/pnpm')]:
    if cache.is_dir() and not cache.is_symlink():shutil.rmtree(cache)
 summary={'applied':apply,'utc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),'current':current['release'],'rollback':previous['release'],'obsoleteContainers':len(obsolete),'unusedBanteraImageTags':removed_tags,'oldBuildSourceBytes':reclaimed_sources,'protectedReleaseDirectories':sorted(str(p) for p in protected_dirs),'volumesOrDatabaseBackupsDeleted':False}
 print(json.dumps(summary,indent=2),flush=True)
 if apply:(Path(current['directory'])/'cleanup-proof.json').write_text(json.dumps(summary,indent=2))
 print(run('df','-h','/'),flush=True)

"""Private, single-worker evaluation queue. No content is written to application logs."""
import fcntl, base64, binascii, contextlib, hmac, json, os, re, shutil, signal, sqlite3, subprocess, threading, time, unicodedata, uuid, wave
from pathlib import Path
import sys
import generation
from fastapi import FastAPI, Request, HTTPException
from fastapi.responses import JSONResponse

DATA=Path(os.environ.get('DATA_DIR','/data')); MODELS=Path(os.environ.get('MODEL_DIR','/models'))
DATA.mkdir(parents=True,exist_ok=True); TEMP=DATA/'tmp'; TEMP.mkdir(exist_ok=True)
worker_lock=(DATA/'worker.lock').open('w')
fcntl.flock(worker_lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
SECRET=os.environ.get('MODEL_SERVICE_KEY',''); MAX_BODY=12*1024*1024; MAX_AUDIO=8*1024*1024
TIMEOUT=int(os.environ.get('JOB_TIMEOUT_SECONDS','900')); lock=threading.RLock(); stop=threading.Event()
IDS=('whisper-small','whisper-large-v3','madlad')
ERRORS={'invalid_audio':'Use a valid WAV, MP3, M4A, Ogg, WebM or FLAC recording.', 'duration':'Audio must be between 0.2 and 60 seconds.', 'timeout':'The job exceeded its execution time limit.', 'inference':'The model could not complete this test. Try a shorter input.', 'restart':'The service restarted. Please submit the test again.', 'cancelled':'Cancelled by the administrator.'}
def db():
 c=sqlite3.connect(DATA/'jobs.sqlite',timeout=10); c.row_factory=sqlite3.Row; c.execute('PRAGMA secure_delete=ON'); return c
with db() as c:
 c.execute('CREATE TABLE IF NOT EXISTS jobs (id TEXT PRIMARY KEY, owner TEXT NOT NULL, model TEXT NOT NULL, status TEXT NOT NULL, created REAL NOT NULL, started REAL, finished REAL, input TEXT, result TEXT, error TEXT, assessment TEXT)')
 generation.initialize(c)
 c.execute("UPDATE jobs SET status='failed',finished=?,error='restart',input=NULL WHERE status IN ('queued','running','cancelling')",(time.time(),))
for p in TEMP.iterdir():
 if p.is_dir(): shutil.rmtree(p)
 else: p.unlink()
def models():
 try: entries=json.loads((MODELS/'manifest.json').read_text())
 except (OSError,ValueError): entries=[]
 byid={e['id']:e for e in entries}
 return [byid.get(i,dict(id=i,name=i,status='not_downloaded')) for i in IDS]
def memory():
 try: return int(Path('/sys/fs/cgroup/memory.current').read_text())
 except (OSError,ValueError): return None

def finish(ident,status,result=None,error=None):
 shutil.rmtree(TEMP/ident,ignore_errors=True)
 with lock, db() as c:
  # A cancellation that raced with successful inference wins.
  row=c.execute('SELECT status,result FROM jobs WHERE id=?',(ident,)).fetchone()
  if row and row['status']=='cancelling': status,result,error='cancelled',None,'cancelled'
  if status!='completed': generation.audio_path(sys.modules[__name__],ident).unlink(missing_ok=True)
  saved=json.dumps(result) if result is not None else (row['result'] if row and status=='failed' else None)
  c.execute('UPDATE jobs SET status=?,result=?,error=?,finished=?,input=NULL,claim=NULL,lease=NULL WHERE id=?',(status,saved,error,time.time(),ident))
def wer(reference,hypothesis):
 def words(s): return re.findall(r"[^\W_]+(?:['’][^\W_]+)*",unicodedata.normalize('NFC',s).lower(),re.UNICODE)
 ref,hyp=words(reference),words(hypothesis)
 if not ref: return None
 row=list(range(len(hyp)+1))
 for i,a in enumerate(ref,1):
  nxt=[i]
  for j,b in enumerate(hyp,1): nxt.append(min(nxt[-1]+1,row[j]+1,row[j-1]+(a!=b)))
  row=nxt
 return {'rate':row[-1]/len(ref),'edits':row[-1],'referenceWords':len(ref),'normalization':'NFC, lowercase, punctuation ignored; macrons retained'}
class JobError(Exception): pass

def execute(ident,args,folder,stdin=None,budget=None):
 """New process group per command; kill and reap on cancellation, timeout or shutdown."""
 output=folder/'stdout'; metrics=folder/'usage'; started=time.monotonic(); peak=memory() or 0
 with output.open('wb') as out, (folder/'stdin').open('wb+') as inp:
  if stdin: inp.write(stdin.encode()); inp.seek(0)
  p=subprocess.Popen(['/usr/bin/time','-f','%U %S %M','-o',str(metrics),*args],stdin=inp,stdout=out,stderr=subprocess.DEVNULL,start_new_session=True)
  try:
   while p.poll() is None:
    with db() as c: status=c.execute('SELECT status FROM jobs WHERE id=?',(ident,)).fetchone()['status']
    if status=='cancelling' or stop.is_set(): raise JobError('cancelled')
    if time.monotonic()-started>(budget or TIMEOUT): raise JobError('timeout')
    peak=max(peak,memory() or 0); time.sleep(.1)
   if p.returncode: raise JobError('inference')
  finally:
   if p.poll() is None:
    with contextlib.suppress(ProcessLookupError): os.killpg(p.pid,signal.SIGKILL)
   p.wait()
 elapsed=time.monotonic()-started
 vals=metrics.read_text().strip().splitlines()[-1].split()
 cpu=float(vals[0])+float(vals[1]); rss=int(vals[2])*1024
 return output.read_text()[:100000],dict(processingSeconds=round(elapsed,3),cpuSeconds=round(cpu,3),averageCpuPercent=round(cpu/elapsed*100,1),processPeakRssBytes=rss,serviceMemoryPeakSampledBytes=peak)

def run_job(row,finish_job=True):
 if row['kind']=='generated': return generation.run_local(sys.modules[__name__],row)
 ident=row['id']; folder=TEMP/ident; folder.mkdir(exist_ok=True); payload=json.loads(row['input']); model=row['model']
 entry=next(e for e in models() if e['id']==model)
 if model=='madlad':
  output,stats=execute(ident,['/opt/bin/bantera-translate',str(MODELS/model)],folder,payload['text'])
  result=json.loads(output); result['sourceText']=payload['text']
 else:
  audio=folder/'upload'; wav=folder/'audio.wav'
  try:
   with audio.open('rb') as stream: header=stream.read(16)
   audio_format = ('wav' if header[:4]==b'RIFF' and header[8:12]==b'WAVE' else 'flac' if header[:4]==b'fLaC' else 'ogg' if header[:4]==b'OggS' else 'matroska' if header[:4]==bytes.fromhex('1a45dfa3') else 'mov' if header[4:8]==b'ftyp' else 'mp3' if header[:3]==b'ID3' or (len(header)>1 and header[0]==255 and header[1]&224==224) else None)
   if audio_format is None: raise JobError('invalid_audio')
   execute(ident,['ffmpeg','-nostdin','-v','error','-threads','1','-protocol_whitelist','file,pipe','-f',audio_format,'-i',str(audio),'-vn','-t','61','-ar','16000','-ac','1','-c:a','pcm_s16le','-threads','1','-y',str(wav)],folder,budget=30)
   with wave.open(str(wav)) as f: duration=f.getnframes()/f.getframerate()
  except JobError as ex:
   if str(ex) in ('timeout','cancelled'): raise
   raise JobError('invalid_audio')
  if not .2<=duration<=60: raise JobError('duration')
  audio.unlink(missing_ok=True)
  _,stats=execute(ident,['/opt/bin/whisper-cli','-m',str(MODELS/model/entry['file']),'-f',str(wav),'-l','mi','-t','2','-ng','-nt','-otxt','-of',str(folder/'transcript')],folder)
  transcript=(folder/'transcript.txt').read_text().strip()
  result={'text':transcript,'language':'mi','audioSeconds':round(duration,3),'realTimeFactor':round(stats['processingSeconds']/duration,3),'reference':payload.get('reference'),'wer':wer(payload['reference'],transcript) if payload.get('reference') else None}
 result.update(stats); result['model']=entry
 if finish_job: finish(ident,'completed',result)
 return result

def cleanup():
 with lock,db() as c:
  generation.expire(sys.modules[__name__],c)
  old=c.execute('SELECT id FROM jobs WHERE created<? AND status NOT IN (\'running\',\'cancelling\')',(time.time()-86400,)).fetchall()
  for row in old:
   shutil.rmtree(TEMP/row['id'],ignore_errors=True); generation.audio_path(sys.modules[__name__],row['id']).unlink(missing_ok=True)
  c.execute('DELETE FROM jobs WHERE created<? AND status NOT IN (\'running\',\'cancelling\')',(time.time()-86400,))
def worker():
 while not stop.is_set():
  try:
   cleanup()
   with lock, db() as c:
    row=c.execute("SELECT * FROM jobs WHERE status='queued' AND NOT (kind='generated' AND stage='text') ORDER BY created LIMIT 1").fetchone()
    if row: c.execute("UPDATE jobs SET status='running',started=? WHERE id=?",(time.time(),row['id']))
   if row:
    try: run_job(row)
    except JobError as e: finish(row['id'],'cancelled' if str(e)=='cancelled' else 'failed',error=str(e))
    except Exception: finish(row['id'],'failed',error='inference')
   else: stop.wait(.25)
  except Exception: stop.wait(1)
@contextlib.asynccontextmanager
async def lifespan(app):
 t=threading.Thread(target=worker,daemon=True);t.start()
 maintenance=threading.Thread(target=generation.maintenance,args=(sys.modules[__name__],),daemon=True);maintenance.start()
 yield
 stop.set();t.join(timeout=5)
app=FastAPI(lifespan=lifespan,docs_url=None,redoc_url=None,openapi_url=None)
@app.middleware('http')
async def authenticate(request,call_next):
 if not SECRET or not hmac.compare_digest(request.headers.get('x-service-key',''),SECRET): return JSONResponse({'error':'Unauthorized'},401)
 try: request.state.owner=str(uuid.UUID(request.headers.get('x-owner-id','')))
 except ValueError: return JSONResponse({'error':'Unauthorized'},401)
 response=await call_next(request);response.headers['Cache-Control']='private, no-store';return response
async def body(request):
 data=bytearray()
 async for part in request.stream():
  data.extend(part)
  if len(data)>MAX_BODY: raise HTTPException(413,'Upload exceeds the 8 MiB audio limit.')
 try: value=json.loads(data)
 except (ValueError,UnicodeDecodeError): raise HTTPException(400,'Invalid request.')
 if not isinstance(value,dict): raise HTTPException(400,'Invalid request.')
 return value
def detail(row):
 d={k:row[k] for k in ('id','model','status','created','started','finished','kind','stage')}
 d.update(result=json.loads(row['result']) if row['result'] else None,error=ERRORS.get(row['error']),assessment=json.loads(row['assessment']) if row['assessment'] else None)
 d['generation']=json.loads(row['generation']) if row['generation'] else None
 d['hasAudio']=row['kind']=='generated' and row['status']=='completed' and generation.audio_path(sys.modules[__name__],row['id']).exists()
 return d
def own(ident,owner,c):
 row=c.execute('SELECT * FROM jobs WHERE id=? AND owner=?',(ident,owner)).fetchone()
 if not row: raise HTTPException(404,'Test not found.')
 return row
@app.get('/status')
def status(request:Request):
 with db() as c: queued=c.execute("SELECT count(*) FROM jobs WHERE status='queued'").fetchone()[0]; running=c.execute("SELECT count(*) FROM jobs WHERE status IN ('running','cancelling')").fetchone()[0]
 return dict(models=models(),queued=queued,running=running,serviceMemoryBytes=memory(),limits=dict(cpuCores=2,memoryBytes=8*1024**3,maxConcurrentJobs=1,maxAudioBytes=MAX_AUDIO,maxAudioSeconds=60,maxTextCharacters=1000,maxInputTokens=256,maxOutputTokens=128,timeoutSeconds=TIMEOUT,retentionHours=24,maxPendingPerAdmin=2,maxPendingGlobal=6,maxJobsPerHour=20))
@app.get('/jobs')
def jobs(request:Request):
 with db() as c: return [detail(r) for r in c.execute('SELECT * FROM jobs WHERE owner=? ORDER BY created DESC LIMIT 50',(request.state.owner,))]
@app.get('/jobs/{ident}')
def job(ident:str,request:Request):
 with db() as c: return detail(own(ident,request.state.owner,c))
@app.post('/jobs',status_code=202)
async def create(request:Request):
 data=await body(request); model=data.get('model'); owner=request.state.owner
 if model not in IDS: raise HTTPException(400,'Choose a supported model.')
 entry=next(e for e in models() if e['id']==model)
 if entry['status']!='ready': raise HTTPException(503,'The selected model is not ready.')
 payload={}; audio=None; kind=data.get('kind','manual'); stage='queued'
 if kind not in ('manual','generated'): raise HTTPException(400,'Invalid test type.')
 if kind=='generated':
  if model=='madlad' or type(data.get('wordCount')) is not int or not 5<=data['wordCount']<=60: raise HTTPException(400,'Choose Whisper Small or large-v3 and 5 to 60 Māori words.')
  if next(e for e in models() if e['id']=='madlad')['status']!='ready': raise HTTPException(503,'The translation model is not ready.')
  payload['wordCount']=data['wordCount'];stage='text'
 elif model=='madlad':
  value=data.get('text')
  if not isinstance(value,str) or not 1<=len(value.strip())<=1000: raise HTTPException(400,'Enter 1 to 1,000 characters.')
  # Reserve model direction tokens for the service.
  if re.search(r'<2[^>]*>',value): raise HTTPException(400,'Language-control tokens are not allowed.')
  payload['text']=value.strip()
 else:
  ref=data.get('reference','')
  if not isinstance(ref,str) or len(ref)>3000: raise HTTPException(400,'Reference transcript must be at most 3,000 characters.')
  try:
   value=data.get('audioBase64','')
   if not isinstance(value,str) or len(value)>MAX_AUDIO*4//3+8: raise ValueError()
   audio=base64.b64decode(value,validate=True)
  except (ValueError,binascii.Error): raise HTTPException(400,'Invalid audio upload.')
  if not 44<=len(audio)<=MAX_AUDIO: raise HTTPException(400,'Audio must be non-empty and at most 8 MiB.')
  payload['reference']=ref
 ident=str(uuid.uuid4()); now=time.time()
 with lock,db() as c:
  if c.execute("SELECT count(*) FROM jobs WHERE status IN ('queued','running','cancelling')").fetchone()[0]>=6 or c.execute("SELECT count(*) FROM jobs WHERE owner=? AND status IN ('queued','running','cancelling')",(owner,)).fetchone()[0]>=2: raise HTTPException(429,'The queue is full. Wait for a test to finish.')
  if c.execute('SELECT count(*) FROM jobs WHERE owner=? AND created>?',(owner,now-3600)).fetchone()[0]>=20 or c.execute('SELECT count(*) FROM jobs WHERE created>?',(now-3600,)).fetchone()[0]>=60: raise HTTPException(429,'Hourly test quota reached. Try again later.')
  folder=TEMP/ident
  try:
   if audio is not None: folder.mkdir();(folder/'upload').write_bytes(audio)
   c.execute('INSERT INTO jobs(id,owner,model,status,created,input,kind,stage) VALUES (?,?,?,\'queued\',?,?,?,?)',(ident,owner,model,now,json.dumps(payload),kind,stage))
  except Exception: shutil.rmtree(folder,ignore_errors=True);raise
 return {'id':ident,'status':'queued'}
@app.delete('/jobs/{ident}')
def cancel(ident:str,request:Request):
 with lock,db() as c:
  row=own(ident,request.state.owner,c)
  if row['status']=='queued':
   shutil.rmtree(TEMP/ident,ignore_errors=True);generation.audio_path(sys.modules[__name__],ident).unlink(missing_ok=True)
   c.execute("UPDATE jobs SET status='cancelled',finished=?,input=NULL,error='cancelled' WHERE id=?",(time.time(),ident))
  elif row['status']=='running': c.execute("UPDATE jobs SET status='cancelling' WHERE id=?",(ident,))
 return {'id':ident,'accepted':True}
@app.put('/jobs/{ident}/assessment')
async def assess(ident:str,request:Request):
 data=await body(request)
 if data.get('rating') not in ('good','mixed','poor','unassessed') or not isinstance(data.get('notes',''),str) or len(data.get('notes',''))>2000: raise HTTPException(400,'Choose a rating and use at most 2,000 characters for notes.')
 with lock,db() as c:
  row=own(ident,request.state.owner,c)
  if (row['model']!='madlad' and row['kind']!='generated') or row['status']!='completed': raise HTTPException(409,'Assess a completed translation.')
  c.execute('UPDATE jobs SET assessment=? WHERE id=?',(json.dumps({'rating':data['rating'],'notes':data.get('notes','')}),ident))
 return {'saved':True}

generation.install(app,sys.modules[__name__])

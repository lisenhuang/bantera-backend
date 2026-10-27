"""Private generation hand-off. Providers run in Bantera's backend, never this container."""
import base64, contextlib, json, shutil, time, uuid
from pathlib import Path
from fastapi import Request, HTTPException
from fastapi.responses import FileResponse

def initialize(c):
 columns={r[1] for r in c.execute('PRAGMA table_info(jobs)')}
 for name,definition in [('kind',"TEXT NOT NULL DEFAULT 'manual'"),('stage',"TEXT NOT NULL DEFAULT 'queued'"),('generation','TEXT'),('claim','TEXT'),('lease','REAL')]:
  if name not in columns: c.execute('ALTER TABLE jobs ADD COLUMN '+name+' '+definition)

def audio_path(s,ident): return s.DATA/'audio'/ident

def expire(s,c):
 now=time.time()
 rows=c.execute("SELECT id,status FROM jobs WHERE kind='generated' AND stage IN ('text','tts') AND status IN ('running','cancelling') AND (status='cancelling' OR lease<? OR started<?)",(now,now-600)).fetchall()
 for row in rows:
  cancelled=row['status']=='cancelling'
  c.execute("UPDATE jobs SET status=?,error=?,finished=?,input=NULL,claim=NULL,lease=NULL WHERE id=?",('cancelled' if cancelled else 'failed','cancelled' if cancelled else 'generation_timeout',now,row['id']))
  shutil.rmtree(s.TEMP/row['id'],ignore_errors=True);audio_path(s,row['id']).unlink(missing_ok=True)

def maintenance(s):
 while not s.stop.wait(1):
  try: s.cleanup()
  except Exception: pass

def run_local(s,row):
 ident=row['id']; generated=json.loads(row['generation']); payload=json.loads(row['input'])
 def stage(value,result=None):
  with s.lock,s.db() as c:
   current=c.execute('SELECT status FROM jobs WHERE id=?',(ident,)).fetchone()
   if not current or current['status']!='running': raise s.JobError('cancelled')
   c.execute('UPDATE jobs SET stage=?,result=? WHERE id=?',(value,json.dumps(result) if result else None,ident))
 stage('stt')
 speech=s.run_job({**dict(row),'kind':'manual','input':json.dumps({'reference':generated['maori']})},finish_job=False)
 partial={**speech,'generated':generated}
 stage('translation',partial)
 if not speech['text'].strip() or len(speech['text'])>1000: raise s.JobError('translation_input')
 translation=s.run_job({**dict(row),'kind':'manual','model':'madlad','input':json.dumps({'text':speech['text']})},finish_job=False)
 result={**speech,'generated':generated,'translation':translation}
 # Each inference keeps its own resource/time metrics; never add RSS high-water marks.
 s.finish(ident,'completed',result)

def install(app,s):
 (s.DATA/'audio').mkdir(exist_ok=True)
 # Recover a crash between saving an audio asset and committing its job state.
 with s.db() as c:
  completed={r[0] for r in c.execute("SELECT id FROM jobs WHERE status='completed'")}
 for asset in (s.DATA/'audio').iterdir():
  if asset.name not in completed: asset.unlink(missing_ok=True)
 s.ERRORS.update(generation_timeout='Text/audio generation was interrupted or exceeded its time limit. Submit a new test.',generation_failed='Text or audio generation could not complete. The app’s configured provider may be unavailable; try again later.',word_count='The text model could not produce the requested word count. Try a different length.',translation_input='The transcript is empty or too long to translate. Review the transcription result.',generation_audio='Generated audio exceeded the 60-second or 8 MiB limit. Use a shorter passage.')
 def claimed(c,ident,request):
  row=s.own(ident,request.state.owner,c)
  if row['kind']!='generated' or row['status']!='running' or row['stage'] not in ('text','tts') or not row['claim'] or request.headers.get('x-generation-claim')!=row['claim'] or (row['lease'] or 0)<time.time(): raise HTTPException(409,'This generation attempt is no longer active.')
  return row
 @app.post('/generation/claim')
 def claim(request:Request):
  with s.lock,s.db() as c:
   expire(s,c)
   if c.execute("SELECT count(*) FROM jobs WHERE kind='generated' AND stage IN ('text','tts') AND status='running'").fetchone()[0]: return None
   row=c.execute("SELECT * FROM jobs WHERE kind='generated' AND stage='text' AND status='queued' ORDER BY created LIMIT 1").fetchone()
   if not row:return None
   secret=str(uuid.uuid4());now=time.time()
   c.execute("UPDATE jobs SET status='running',started=?,claim=?,lease=? WHERE id=?",(now,secret,now+60,row['id']))
   return {'id':row['id'],'owner':row['owner'],'claim':secret,'wordCount':json.loads(row['input'])['wordCount']}
 @app.post('/generation/{ident}/heartbeat')
 def heartbeat(ident:str,request:Request):
  with s.lock,s.db() as c:
   claimed(c,ident,request);c.execute('UPDATE jobs SET lease=? WHERE id=?',(time.time()+60,ident))
  return {'active':True}
 @app.post('/generation/{ident}/text')
 async def text(ident:str,request:Request):
  data=await s.body(request)
  if not isinstance(data.get('maori'),str) or not 1<=len(data['maori'])<=1000 or not isinstance(data.get('english'),str) or not 1<=len(data['english'])<=2000: raise HTTPException(400,'Invalid generated text.')
  with s.lock,s.db() as c:
   claimed(c,ident,request);c.execute("UPDATE jobs SET generation=?,stage='tts' WHERE id=?",(json.dumps(data),ident))
  return {'saved':True}
 @app.post('/generation/{ident}/prepared')
 async def prepared(ident:str,request:Request):
  data=await s.body(request)
  try: audio=base64.b64decode(data.get('audioBase64',''),validate=True)
  except (ValueError,TypeError): raise HTTPException(400,'Invalid generated audio.')
  if not 44<=len(audio)<=s.MAX_AUDIO or not isinstance(data.get('audioDurationMs'),int) or not 200<=data['audioDurationMs']<=60000: raise HTTPException(400,'Generated audio exceeds test limits.')
  with s.lock,s.db() as c:
   row=claimed(c,ident,request)
   if row['stage']!='tts' or not row['generation']:raise HTTPException(409,'Generate the text first.')
   gen=json.loads(row['generation'])
   gen.update({k:data.get(k) for k in ['audioDurationMs','audioSeconds','configuredAudioModel','fallbackAudioModel','actualAudioModel','voice','audioContentType']})
   if gen['audioContentType'] not in ('audio/mpeg','audio/wav'):raise HTTPException(400,'Unsupported generated audio format.')
   folder=s.TEMP/ident;folder.mkdir(exist_ok=True)
   try:
    (folder/'upload').write_bytes(audio);audio_path(s,ident).write_bytes(audio)
    c.execute("UPDATE jobs SET generation=?,stage='stt',status='queued',claim=NULL,lease=NULL WHERE id=?",(json.dumps(gen),ident))
   except Exception:
    shutil.rmtree(folder,ignore_errors=True);audio_path(s,ident).unlink(missing_ok=True);raise
  return {'queued':True}
 @app.post('/generation/{ident}/failed')
 async def failed(ident:str,request:Request):
  data=await s.body(request);error=data.get('error','generation_failed')
  if error not in ('generation_failed','generation_timeout','word_count','generation_audio'):error='generation_failed'
  with s.lock,s.db() as c:
   claimed(c,ident,request)
   c.execute("UPDATE jobs SET status='failed',error=?,finished=?,input=NULL,claim=NULL,lease=NULL WHERE id=?",(error,time.time(),ident))
  return {'failed':True}
 @app.get('/jobs/{ident}/audio')
 def audio(ident:str,request:Request):
  with s.lock,s.db() as c:
   row=s.own(ident,request.state.owner,c)
   if row['kind']!='generated' or row['status']!='completed' or not audio_path(s,ident).exists():raise HTTPException(404,'Generated audio is not available.')
   gen=json.loads(row['generation'])
   return FileResponse(audio_path(s,ident),media_type=gen['audioContentType'],headers={'Cache-Control':'private, no-store'})

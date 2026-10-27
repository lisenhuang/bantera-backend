import base64, io, json, os, shutil, tempfile, threading, time, unittest, uuid, wave
from pathlib import Path
os.environ['DATA_DIR']=tempfile.mkdtemp(prefix='model-tests-')
os.environ['MODEL_DIR']=tempfile.mkdtemp(prefix='model-fixtures-')
os.environ['MODEL_SERVICE_KEY']='isolated-test-key'
import service as s
from fastapi.testclient import TestClient
class Tests(unittest.TestCase):
 def setUp(self):
  self.owner=str(uuid.uuid4()); self.headers={'x-service-key':'isolated-test-key','x-owner-id':self.owner}; self.client=TestClient(s.app)
  with s.db() as c:c.execute('DELETE FROM jobs')
  for p in s.TEMP.iterdir(): shutil.rmtree(p)
  (s.MODELS/'manifest.json').write_text(json.dumps([{'id':i,'status':'ready','file':'fake'} for i in s.IDS]))
 def post(self,model='madlad',**kw):return self.client.post('/jobs',headers=self.headers,json={'model':model,'text':'Kia ora',**kw})
 def row(self,ident):
  with s.db() as c:return c.execute('SELECT * FROM jobs WHERE id=?',(ident,)).fetchone()
 def test_private_auth_all_routes(self):
  for method,path in [('get','/status'),('get','/jobs'),('post','/jobs'),('get','/jobs/a'),('delete','/jobs/a'),('put','/jobs/a/assessment')]:
   self.assertEqual(getattr(self.client,method)(path).status_code,401)
 def test_ownership_all_job_operations(self):
  ident=self.post().json()['id']; h={**self.headers,'x-owner-id':str(uuid.uuid4())}
  self.assertEqual(self.client.get('/jobs',headers=h).json(),[])
  for method,path in [('get',f'/jobs/{ident}'),('delete',f'/jobs/{ident}'),('put',f'/jobs/{ident}/assessment')]:
   r=getattr(self.client,method)(path,headers=h,**({'json':{'rating':'good'}} if method=='put' else {})); self.assertEqual(r.status_code,404)
 def test_input_validation(self):
  for params in [{'model':'small.en'},{'text':' '},{'text':'a'*1001},{'text':'<2fr> Kia ora'},{'model':'whisper-small','audioBase64':'!!'},{'model':'whisper-small','audioBase64':base64.b64encode(b'a'*45).decode(),'reference':'a'*3001}]:self.assertEqual(self.post(**params).status_code,400)
  self.assertEqual(self.client.post('/jobs',headers=self.headers,content=b'x'*(s.MAX_BODY+1)).status_code,413)
 def test_not_ready(self):
  (s.MODELS/'manifest.json').write_text('[]'); self.assertEqual(self.post().status_code,503)
 def test_quota_and_queue_atomic(self):
  result=[]
  threads=[threading.Thread(target=lambda:result.append(self.post().status_code)) for _ in range(8)]
  for t in threads:t.start()
  for t in threads:t.join()
  self.assertEqual(result.count(202),2);self.assertEqual(result.count(429),6)
 def test_global_queue_bound_across_owners(self):
  for _ in range(3):
   self.headers['x-owner-id']=str(uuid.uuid4())
   self.assertEqual(self.post().status_code,202)
   self.assertEqual(self.post().status_code,202)
  self.headers['x-owner-id']=str(uuid.uuid4())
  self.assertEqual(self.post().status_code,429)
 def test_hourly_quota(self):
  with s.db() as c:
   for i in range(20):c.execute('INSERT INTO jobs(id,owner,model,status,created) VALUES(?,?,?,\'completed\',?)',(str(uuid.uuid4()),self.owner,'madlad',time.time()))
  self.assertEqual(self.post().status_code,429)
 def test_queued_cancel_removes_audio(self):
  ident=self.post(model='whisper-small',audioBase64=base64.b64encode(b'a'*100).decode()).json()['id'];self.assertTrue((s.TEMP/ident/'upload').exists())
  self.assertEqual(self.client.delete('/jobs/'+ident,headers=self.headers).status_code,200)
  self.assertEqual(self.row(ident)['status'],'cancelled');self.assertFalse((s.TEMP/ident).exists());self.assertIsNone(self.row(ident)['input'])
 def test_running_cancel_kills_process_group(self):
  ident=self.post().json()['id'];folder=s.TEMP/ident;folder.mkdir()
  with s.db() as c:c.execute("UPDATE jobs SET status='running' WHERE id=?",(ident,))
  errors=[]
  def run():
   try:s.execute(ident,['sh','-c','sleep 60 & echo $! > '+str(folder/'child')+'; wait'],folder)
   except s.JobError as e:errors.append(str(e))
  t=threading.Thread(target=run);t.start()
  for _ in range(50):
   if (folder/'child').exists():break
   time.sleep(.02)
  self.client.delete('/jobs/'+ident,headers=self.headers);t.join(3);self.assertFalse(t.is_alive());self.assertEqual(errors,['cancelled'])
 def test_timeout_and_failure_recovery(self):
  ident=self.post().json()['id'];folder=s.TEMP/ident;folder.mkdir()
  with self.assertRaisesRegex(s.JobError,'timeout'):s.execute(ident,['sleep','60'],folder,budget=.2)
  with self.assertRaisesRegex(s.JobError,'inference'):s.execute(ident,['false'],folder)
  text,metrics=s.execute(ident,['echo','recovered'],folder);self.assertEqual(text.strip(),'recovered');self.assertGreaterEqual(metrics['processPeakRssBytes'],0)
 def test_invalid_audio_cleanup(self):
  ident=self.post(model='whisper-small',audioBase64=base64.b64encode(b'a'*100).decode()).json()['id']
  with self.assertRaisesRegex(s.JobError,'invalid_audio'):s.run_job(self.row(ident))
  s.finish(ident,'failed',error='invalid_audio');self.assertFalse((s.TEMP/ident).exists());self.assertIsNone(self.row(ident)['input'])
 def test_duration_enforced_after_decode(self):
  data=io.BytesIO()
  with wave.open(data,'wb') as f:f.setnchannels(1);f.setsampwidth(2);f.setframerate(16000);f.writeframes(b'\0'*(16000*2*61))
  ident=self.post(model='whisper-small',audioBase64=base64.b64encode(data.getvalue()).decode()).json()['id']
  with self.assertRaisesRegex(s.JobError,'duration'):s.run_job(self.row(ident))
 def test_cancel_race_wins(self):
  ident=self.post().json()['id']
  with s.db() as c:c.execute("UPDATE jobs SET status='cancelling' WHERE id=?",(ident,))
  s.finish(ident,'completed',{'text':'should not persist'});self.assertEqual(self.row(ident)['status'],'cancelled');self.assertIsNone(self.row(ident)['result'])
 def test_wer_macrons_punctuation_and_insertions(self):
  self.assertEqual(s.wer('KIA ORA, Māori.', 'kia ora Māori')['rate'],0)
  self.assertGreater(s.wer('Māori','Maori added extra')['rate'],1)
  self.assertIsNone(s.wer('...','kia ora'))
 def test_assessment_and_retention(self):
  ident=self.post().json()['id'];s.finish(ident,'completed',{'text':'Hello'})
  self.assertEqual(self.client.put('/jobs/'+ident+'/assessment',headers=self.headers,json={'rating':'mixed','notes':'Review'}).status_code,200)
  self.assertEqual(json.loads(self.row(ident)['assessment'])['rating'],'mixed')
  with s.db() as c:c.execute('UPDATE jobs SET created=? WHERE id=?',(time.time()-90000,ident))
  s.cleanup();self.assertIsNone(self.row(ident))
if __name__=='__main__':unittest.main()

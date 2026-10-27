import base64,json,time,uuid,unittest
from unittest.mock import patch
from test_service import Tests
import service as s
import generation as g

class GenerationTests(Tests):
 def create_generated(self):
  r=self.post(model='whisper-small',kind='generated',wordCount=10)
  self.assertEqual(r.status_code,202);return r.json()['id']
 def claim(self):
  r=self.client.post('/generation/claim',headers=self.headers,json={});self.assertEqual(r.status_code,200)
  value=r.json();return value, {**self.headers,'x-generation-claim':value['claim']} if value else self.headers
 def prepare(self,ident,h):
  self.assertEqual(self.client.post('/generation/'+ident+'/text',headers=h,json={'maori':'Kia ora','english':'Hello','wordCount':10}).status_code,200)
  return self.client.post('/generation/'+ident+'/prepared',headers=h,json={'audioBase64':base64.b64encode(b'RIFF'+b'0'*100).decode(),'audioDurationMs':2000,'audioContentType':'audio/wav'})
 def test_generated_inputs_and_readiness(self):
  for words in [True,4,61,10.5,'10',None]:self.assertEqual(self.post(model='whisper-small',kind='generated',wordCount=words).status_code,400)
  self.assertEqual(self.post(kind='generated',wordCount=10).status_code,400)
  (s.MODELS/'manifest.json').write_text(json.dumps([{'id':'whisper-small','status':'ready'}]))
  self.assertEqual(self.post(model='whisper-small',kind='generated',wordCount=10).status_code,503)
 def test_claim_is_single_and_not_public(self):
  ident=self.create_generated();self.create_generated();claimed,h=self.claim();self.assertEqual(claimed['id'],ident)
  self.assertIsNone(self.claim()[0])
  self.assertEqual(self.client.post('/generation/claim').status_code,401)
  for action in ['heartbeat','text','prepared','failed']:
   self.assertEqual(self.client.post('/generation/'+ident+'/'+action,json={}).status_code,401)
  self.assertEqual(self.client.post('/generation/'+ident+'/heartbeat',headers=self.headers,json={}).status_code,409)
  self.assertEqual(self.client.post('/generation/'+ident+'/heartbeat',headers={**h,'x-owner-id':str(uuid.uuid4())},json={}).status_code,404)
  self.assertEqual(self.client.post('/generation/'+ident+'/heartbeat',headers=h,json={}).status_code,200)
 def test_cancel_generation_rejects_late_provider_response(self):
  ident=self.create_generated();_,h=self.claim();self.client.delete('/jobs/'+ident,headers=self.headers);s.cleanup()
  self.assertEqual(self.row(ident)['status'],'cancelled')
  self.assertEqual(self.client.post('/generation/'+ident+'/text',headers=h,json={'maori':'Kia ora','english':'Hello'}).status_code,409)
  self.assertFalse(g.audio_path(s,ident).exists())
 def test_generation_lease_expires_and_queue_recovers(self):
  ident=self.create_generated();_,h=self.claim()
  with s.db() as c:c.execute('UPDATE jobs SET lease=? WHERE id=?',(time.time()-1,ident))
  s.cleanup();self.assertEqual(self.row(ident)['error'],'generation_timeout')
  another=self.create_generated();self.assertEqual(self.claim()[0]['id'],another)
 def test_prepared_audio_ownership_and_cleanup(self):
  ident=self.create_generated();_,h=self.claim();self.assertEqual(self.prepare(ident,h).status_code,200)
  self.assertEqual(self.row(ident)['stage'],'stt');self.assertTrue(g.audio_path(s,ident).exists())
  self.assertEqual(self.client.get('/jobs/'+ident+'/audio',headers=self.headers).status_code,404)
  s.finish(ident,'completed',{'text':'Kia ora'})
  self.assertEqual(self.client.get('/jobs/'+ident+'/audio',headers=self.headers).status_code,200)
  self.assertEqual(self.client.get('/jobs/'+ident+'/audio',headers={**self.headers,'x-owner-id':str(uuid.uuid4())}).status_code,404)
  self.assertEqual(self.client.get('/jobs/'+ident+'/audio').status_code,401)
  r=self.client.get('/jobs/'+ident+'/audio',headers={**self.headers,'Range':'bytes=0-9'})
  self.assertEqual(r.status_code,206);self.assertEqual(len(r.content),10)
  with s.db() as c:c.execute('UPDATE jobs SET created=? WHERE id=?',(time.time()-90000,ident))
  s.cleanup();self.assertFalse(g.audio_path(s,ident).exists())
 def test_prepared_cancellation_removes_both_audio_copies(self):
  ident=self.create_generated();_,h=self.claim();self.prepare(ident,h)
  self.client.delete('/jobs/'+ident,headers=self.headers)
  self.assertFalse(g.audio_path(s,ident).exists());self.assertFalse((s.TEMP/ident).exists())
 def test_pipeline_translates_transcript_not_reference(self):
  ident=self.create_generated();_,h=self.claim();self.prepare(ident,h)
  with s.db() as c:c.execute("UPDATE jobs SET status='running' WHERE id=?",(ident,))
  calls=[]
  def infer(row,finish_job):
   calls.append(row);return {'text':'Whisper transcript','processingSeconds':2} if row['model']!='madlad' else {'text':'Translation','processingSeconds':3}
  with patch.object(s,'run_job',side_effect=infer):g.run_local(s,self.row(ident))
  self.assertEqual(json.loads(calls[1]['input'])['text'],'Whisper transcript')
  result=json.loads(self.row(ident)['result']);self.assertEqual(result['translation']['text'],'Translation');self.assertEqual(self.row(ident)['status'],'completed')
 def test_translation_failure_keeps_transcript_and_removes_audio(self):
  ident=self.create_generated();_,h=self.claim();self.prepare(ident,h)
  with s.db() as c:c.execute("UPDATE jobs SET status='running' WHERE id=?",(ident,))
  with patch.object(s,'run_job',side_effect=[{'text':'Whisper transcript'},s.JobError('timeout')]):
   with self.assertRaises(s.JobError):g.run_local(s,self.row(ident))
  s.finish(ident,'failed',error='timeout');self.assertEqual(json.loads(self.row(ident)['result'])['text'],'Whisper transcript')
  self.assertFalse(g.audio_path(s,ident).exists());self.assertFalse((s.TEMP/ident).exists())
if __name__=='__main__':unittest.main()

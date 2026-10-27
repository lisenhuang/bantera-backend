import subprocess,secrets,os,json
from pathlib import Path
os.umask(0o077)
root=Path('/home/ubuntu/bantera-model-service');state=Path('/home/ubuntu/bantera-model-state');state.mkdir(exist_ok=True)
keyfile=root/'service.env'
if not keyfile.exists():keyfile.write_text('MODEL_SERVICE_KEY='+secrets.token_urlsafe(48)+'\n')
subprocess.run(['sudo','chown','10001:10001',str(state)],check=True)
if subprocess.run(['docker','network','inspect','bantera-model-private'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL).returncode:subprocess.run(['docker','network','create','--internal','bantera-model-private'],check=True)
subprocess.run(['docker','run','-d','--name','bantera-model-service-1','--init','--network','bantera-model-private','--network-alias','bantera-model-service','--cpus','2','--memory','8g','--memory-swap','8g','--pids-limit','128','--read-only','--tmpfs','/tmp:rw,noexec,nosuid,size=64m','--cap-drop','ALL','--security-opt','no-new-privileges','--restart','unless-stopped','--log-opt','max-size=10m','--log-opt','max-file=3','--env-file',str(keyfile),'-v',str(state)+':/data','-v','/home/ubuntu/bantera-model-data:/models:ro','bantera-model-service:1.0.1'],check=True,stdout=subprocess.DEVNULL)
print('Started private model service: 2 CPUs, 8 GiB, no published ports, internal network only.')

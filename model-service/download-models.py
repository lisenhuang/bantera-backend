"""Pinned official repositories. Atomic download; retain source cards and checksums."""
import hashlib, json, os, urllib.request
from pathlib import Path
root = Path('/models'); root.mkdir(exist_ok=True)
whisper_revision = '5359861c739e955e79d9a303bcbc70fb988958b1'
madlad_revision = 'fa184c675da0b5c9e1c8694fccd4e12e2d422094'
models = [
 ('whisper-small','Whisper Small (multilingual)','ggerganov/whisper.cpp',whisper_revision,'ggml-small-q5_1.bin','Q5_1','MIT','whisper.cpp 1.9.5'),
 ('whisper-large-v3','Whisper large-v3 (multilingual)','ggerganov/whisper.cpp',whisper_revision,'ggml-large-v3-q5_0.bin','Q5_0','MIT','whisper.cpp 1.9.5'),
 ('madlad','Google MADLAD-400-3B-MT','google/madlad400-3b-mt',madlad_revision,'model-q4k.gguf','Q4_K GGUF','Apache-2.0','Candle 0.9.1')]
def download(url, path):
 if path.exists(): return
 tmp=Path(str(path)+'.partial')
 with urllib.request.urlopen(url,timeout=120) as src, tmp.open('wb') as out:
  while chunk:=src.read(1024*1024): out.write(chunk)
 tmp.rename(path)
manifest=[]
for ident,name,repo,revision,file,quant,lic,runtime in models:
 folder=root/ident; folder.mkdir(exist_ok=True)
 entry=dict(id=ident,name=name,source='https://huggingface.co/'+repo,revision=revision,file=file,quantization=quant,license=lic,runtime=runtime,status='downloading')
 manifest.append(entry); (root/'manifest.json').write_text(json.dumps(manifest,indent=2))
 for f in [file,'README.md']+(['config.json','tokenizer.json'] if ident=='madlad' else []):
  download(f'https://huggingface.co/{repo}/resolve/{revision}/{f}',folder/f)
 entry.update(status='ready',sizeBytes=(folder/file).stat().st_size,sha256=hashlib.file_digest((folder/file).open('rb'),'sha256').hexdigest())
 (root/'manifest.json').write_text(json.dumps(manifest,indent=2)); print(ident,entry['sizeBytes'],flush=True)

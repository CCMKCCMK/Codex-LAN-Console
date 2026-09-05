"""Package explicit build outputs, excluding local state and debug symbols."""
import hashlib, os, pathlib, re, zipfile
ROOT=pathlib.Path(__file__).resolve().parents[1]
release=ROOT/'release'; source=release/'PublicWindows-1.9.0'
patterns=[rb'gh[pousr]_[A-Za-z0-9]{30,}',rb'(?<![A-Za-z0-9_-])sk-(?:cp-|proj-)?[A-Za-z0-9_-]{35,200}(?![A-Za-z0-9_-])',rb'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----']
for value in os.environ.get('AUDIT_PRIVATE_MARKERS','').split('|'):
    if value:patterns.append(re.escape(value.encode()))
files=[]
for file in source.rglob('*'):
    if not file.is_file():continue
    name=file.relative_to(source)
    if file.suffix.lower()=='.pdb' or file.name=='appsettings.Development.json' or file.name.endswith('.test.cjs'):continue
    if file.suffix.lower() in {'.jks','.key','.pem','.jsonl','.log','.bak'} or any(p in {'Scooter','Uploads','AppData'} for p in name.parts):
        raise SystemExit('Unexpected private file: '+str(name))
    body=file.read_bytes()
    if any(re.search(pattern,body) for pattern in patterns):raise SystemExit('Sensitive marker in artifact: '+str(name))
    files.append((file,str(name)))
output=release/'Codex-LAN-Console-Windows-v1.9.0.zip'
with zipfile.ZipFile(output,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    for file,name in files:z.write(file,name)
apk=release/'Codex-LAN-Console-v1.9.0.apk'
with zipfile.ZipFile(apk) as z:
    for name in z.namelist():
        if name.endswith(('.dex','.xml','.json','.txt')) and any(re.search(p,z.read(name)) for p in patterns):
            raise SystemExit('Sensitive marker in Android artifact: '+name)
sums=[]
for file in [output,apk]:
    sums.append(hashlib.sha256(file.read_bytes()).hexdigest()+'  '+file.name)
(release/'SHA256SUMS-1.9.0.txt').write_text('\n'.join(sums)+'\n',encoding='utf-8')
print('Packaged '+str(len(files))+' reviewed build files. No keys, logs or PDBs.\n'+ '\n'.join(sums))

"""Report only locations/types, never matched secret values. Scan worktree and Git history."""
import json, os, pathlib, re, subprocess
ROOT = pathlib.Path(__file__).resolve().parents[1]
SKIP = {'.git','bin','obj','build','.gradle','release','tmp','artifacts','node_modules'}
PATTERNS = {
    'github-token': rb'gh[pousr]_[A-Za-z0-9]{30,}',
    'api-secret': rb'(?<![A-Za-z0-9_-])sk-(?:cp-|proj-)?[A-Za-z0-9_-]{35,200}(?![A-Za-z0-9_-])',
    'private-key': rb'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
}
if os.environ.get('AUDIT_PRIVATE_CODE'):
    PATTERNS['personal-code'] = re.escape(os.environ['AUDIT_PRIVATE_CODE'].encode())
for i, value in enumerate(os.environ.get('AUDIT_PRIVATE_MARKERS', '').split('|')):
    if value: PATTERNS['private-marker-'+str(i)] = re.escape(value.encode())
def findings(body, location):
    for label, pattern in PATTERNS.items():
        for m in re.finditer(pattern, body):
            yield {'location':location,'kind':label,'line':body[:m.start()].count(b'\n')+1}
results = []
for directory, dirs, files in os.walk(ROOT):
    dirs[:] = [d for d in dirs if d not in SKIP]
    for name in files:
        file = pathlib.Path(directory) / name
        if file.suffix.lower() in {'.jks','.keystore','.p12','.pem','.key'}:
            ignored = subprocess.run(['git','check-ignore','--quiet',str(file)],cwd=ROOT).returncode == 0
            results.append({'location':str(file.relative_to(ROOT)),'kind':'excluded-private-key-file' if ignored else 'UNIGNORED-private-key-file'})
            continue
        if file.stat().st_size < 8*1024*1024:
            results.extend(findings(file.read_bytes(), str(file.relative_to(ROOT))))
def git(*args): return subprocess.check_output(['git',*args],cwd=ROOT)
for entry in git('rev-list','--objects','--all').decode().splitlines():
    oid, _, name = entry.partition(' ')
    kind = git('cat-file','-t',oid).decode().strip()
    if kind not in {'blob','commit'}: continue
    results.extend(findings(git('cat-file','-p',oid), 'history:'+oid[:10]+':'+name))
print(json.dumps({'findings':results},ensure_ascii=False,indent=2))
raise SystemExit(1 if any(x['kind'] != 'excluded-private-key-file' for x in results) else 0)

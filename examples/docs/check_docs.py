"""Check local Markdown links and compile all complete tutorial programs."""
import os
from pathlib import Path
import re
import subprocess
import tempfile
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[2]
EXE = os.environ["MAIL_EXE"]
pages = [ROOT / "README.md", *sorted((ROOT / "docs").glob("*.md")),
         ROOT / "examples/docs/README.md"]
programs = sorted((ROOT / "examples/docs").glob("*.mail"))
programs.append(ROOT / "examples/composition/main.mail")
errors = []
fences = 0

def validate(path):
    result = subprocess.run([EXE, "validate", str(path)], capture_output=True, text=True)
    if result.returncode:
        errors.append(f"{path}: {result.stdout}{result.stderr}")

for page in pages:
    content = page.read_text(encoding="utf-8")
    prose = re.sub(r"```.*?```", "", content, flags=re.S)
    for target in re.findall(r"\]\(([^)]+)\)", prose):
        if "://" in target or target.startswith("#"):
            continue
        path = unquote(target.split("#", 1)[0])
        if not (page.parent / path).exists():
            errors.append(f"{page}: broken link {target}")
    for source in re.findall(r"```mail\s*\n(.*?)```", content, flags=re.S):
        with tempfile.TemporaryDirectory() as folder:
            example = Path(folder) / "snippet.mail"
            example.write_text(source, encoding="utf-8")
            validate(example)
            fences += 1
for program in programs:
    validate(program)
if errors:
    raise SystemExit("\n".join(errors))
print(f"Checked {len(pages)} Markdown pages, {len(programs)} entry files, {fences} complete MAIL fences.")

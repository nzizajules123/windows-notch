"""Saves what the notch wrote for you where you asked: Desktop, Documents, Downloads, a sub-folder, or a full path.
Word files (.docx) are built directly; everything else is plain UTF-8 text. Never overwrites unless asked, never
writes into system folders, never creates programs or scripts."""
import os
import re
import zipfile
from xml.sax.saxutils import escape

from .system import open_uri, xdg_dir

BLOCKED = {".sh", ".bash", ".zsh", ".desktop", ".run", ".bin", ".appimage", ".so", ".deb", ".rpm", ".elf", ".service",
           ".exe", ".bat", ".cmd", ".ps1", ".jar"}
PROTECTED = ("/bin", "/boot", "/dev", "/etc", "/lib", "/lib64", "/opt", "/proc", "/root", "/run", "/sbin", "/snap",
             "/srv", "/sys", "/usr", "/var")


def resolve_folder(folder: str) -> str:
    f = (folder or "").strip().strip('"').replace("\\", "/")
    f = re.sub(r"^(my|the)\s+", "", f, flags=re.I)
    if not f:
        return os.path.join(xdg_dir("documents"), "Notch")
    f = os.path.expanduser(f)
    if os.path.isabs(f):
        return os.path.normpath(f)
    first, _, rest = f.partition("/")
    names = {"desktop": "desktop", "documents": "documents", "docs": "documents", "downloads": "downloads",
             "download": "downloads", "pictures": "pictures", "photos": "pictures", "music": "music", "videos": "videos"}
    if first.lower() in names:
        return os.path.normpath(os.path.join(xdg_dir(names[first.lower()]), rest))
    return os.path.normpath(os.path.join(xdg_dir("documents"), f))


def save(content: str, file_name: str, folder: str, fmt: str, overwrite: bool) -> str:
    if not content.strip():
        raise ValueError("There was nothing to write.")
    d = resolve_folder(folder)
    if d == "/" or any(d == p or d.startswith(p + "/") for p in PROTECTED):
        raise ValueError("That's a system folder; pick Documents, Desktop or another folder of yours.")
    name = re.sub(r'[\\/:*?"<>|]', "", file_name or "").strip().rstrip(".") or "Notch note"
    ext = os.path.splitext(name)[1].lower()
    if not ext or len(ext) > 6:
        ext = "." + ((fmt or "docx").strip().lstrip(".").lower())
        name += ext
    if ext in BLOCKED:
        raise ValueError(f"I don't create {ext} files, because they could run as programs.")
    os.makedirs(d, exist_ok=True)
    path = os.path.join(d, name)
    if os.path.exists(path) and not overwrite:
        stem = os.path.splitext(name)[0]
        i = 2
        while os.path.exists(path):
            path = os.path.join(d, f"{stem} ({i}){ext}")
            i += 1
    if ext == ".docx":
        _docx(path, content)
    else:
        with open(path, "w", encoding="utf-8") as f:
            f.write(content.replace("\r\n", "\n"))
    os.chmod(path, 0o644)
    return path


def _docx(path, content):
    body = []
    for raw in content.replace("\r\n", "\n").split("\n"):
        line, heading = raw.rstrip(), 0
        m = re.match(r"^(#{1,3})\s+(.*)$", line)
        if m:
            heading, line = len(m.group(1)), m.group(2)
        b = re.match(r"^\s*[-*•]\s+(.*)$", line)
        if b:
            line = "•  " + b.group(1)
        size = {1: 36, 2: 30, 3: 26}.get(heading, 0)
        runs = []
        pos = 0
        parts = []
        for mm in re.finditer(r"\*\*([^*]+)\*\*", line):
            if mm.start() > pos:
                parts.append((line[pos:mm.start()], False))
            parts.append((mm.group(1), True))
            pos = mm.end()
        if pos < len(line) or not parts:
            parts.append((line[pos:], False))
        for text, bold in parts:
            rpr = ("<w:b/>" if bold or heading else "") + (f'<w:sz w:val="{size}"/>' if size else "")
            runs.append(f'<w:r><w:rPr>{rpr}</w:rPr><w:t xml:space="preserve">{escape(text)}</w:t></w:r>')
        body.append(f'<w:p><w:pPr><w:spacing w:after="{160 if heading else 120}"/></w:pPr>{"".join(runs)}</w:p>')
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        z.writestr("_rels/.rels", '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
        z.writestr("word/document.xml", '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>'
                   + "".join(body) + '<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="708" w:footer="708" w:gutter="0"/></w:sectPr></w:body></w:document>')


def open_path(path: str) -> str:
    p = os.path.expanduser(path.strip().strip('"'))
    if not os.path.exists(p):
        p = resolve_folder(path)
    if not os.path.exists(p):
        raise ValueError("That file or folder doesn't exist.")
    if os.path.isfile(p) and (os.path.splitext(p)[1].lower() in BLOCKED or os.access(p, os.X_OK)):
        raise ValueError("I don't open programs or scripts.")
    open_uri("file://" + os.path.abspath(p))
    return p

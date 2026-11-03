"""Safe Updater tool check: runs before and after a Python update.

Each check uses only what this PC already has. A missing library or model is SKIP, never FAIL, and nothing
is downloaded. The app compares the runs: a check that passed before and fails after means the update broke
it, and the update is rolled back.
"""
import glob
import importlib.util
import os
import site
import sys
from importlib.metadata import version

sys.stdout.reconfigure(encoding="utf-8")
work = os.path.join(os.environ.get("TEMP", "."), "safe-updater-check")
os.makedirs(work, exist_ok=True)
wav = sys.argv[1] if len(sys.argv) > 1 else ""
results = []


class Skip(Exception):
    pass


def need(*modules):
    for m in modules:
        if importlib.util.find_spec(m) is None:
            raise Skip(f"{m} not installed")


def check(name, fn):
    try:
        results.append(f"PASS {name}: {fn()}")
    except Skip as s:
        results.append(f"SKIP {name}: {s}")
    except Exception as e:  # report every failure, keep going
        results.append(f"FAIL {name}: {type(e).__name__}: {e}")


def t_pymupdf():
    need("fitz")
    import fitz
    doc = fitz.open()
    doc.new_page().insert_text((72, 72), "check page")
    pdf = os.path.join(work, "t.pdf")
    doc.save(pdf)
    d2 = fitz.open(pdf)
    d2[0].get_pixmap(dpi=72).save(os.path.join(work, "t.png"))
    return f"{version('pymupdf')} text={d2[0].get_text().strip()!r}"


def t_pillow():
    need("PIL")
    from PIL import Image
    p = os.path.join(work, "p.png")
    Image.new("RGB", (40, 20), "white").save(p)
    return f"{version('pillow')} size={Image.open(p).size}"


def t_pypdf():
    need("pypdf", "fitz")
    from pypdf import PdfReader
    return f"{version('pypdf')} pages={len(PdfReader(os.path.join(work, 't.pdf')).pages)}"


def t_docx():
    need("docx")
    import docx
    p = os.path.join(work, "t.docx")
    d = docx.Document()
    d.add_paragraph("check docx")
    d.save(p)
    return f"{version('python-docx')} read back={docx.Document(p).paragraphs[0].text!r}"


def t_pptx():
    need("pptx")
    from pptx import Presentation
    p = os.path.join(work, "t.pptx")
    pr = Presentation()
    pr.slides.add_slide(pr.slide_layouts[0]).shapes.title.text = "check pptx"
    pr.save(p)
    return f"{version('python-pptx')} slides={len(Presentation(p).slides)}"


def t_excel():
    need("openpyxl")
    import openpyxl
    p = os.path.join(work, "t1.xlsx")
    wb = openpyxl.Workbook()
    wb.active.append(["a", "b"])
    wb.active.append([1, 2])
    wb.save(p)
    out = f"openpyxl {version('openpyxl')}"
    if importlib.util.find_spec("pandas"):
        import pandas as pd
        out += f", pandas {version('pandas')}, sum={int(pd.read_excel(p).sum().sum())}"
    if importlib.util.find_spec("xlsxwriter"):
        import xlsxwriter
        w = xlsxwriter.Workbook(os.path.join(work, "t2.xlsx"))
        w.add_worksheet().write(0, 0, "x")
        w.close()
        out += f", xlsxwriter {version('xlsxwriter')}"
    return out


def t_numpy():
    need("numpy")
    import numpy as np
    return f"{version('numpy')} sum={int(np.arange(10).sum())}"


def t_lxml():
    need("lxml")
    from lxml import etree
    return f"{version('lxml')} tag={etree.fromstring(b'<a><b/></a>')[0].tag}"


def t_requests():
    need("requests")
    import requests
    try:
        code = requests.get("https://pypi.org/simple/pip/", timeout=20).status_code
    except requests.exceptions.ConnectionError:
        raise Skip("offline")
    return f"{version('requests')} pypi status={code}"


def t_av():
    need("av")
    if not wav or not os.path.isfile(wav):
        raise Skip("no test audio")
    import av
    with av.open(wav) as c:
        frames = sum(1 for _ in c.decode(audio=0))
    return f"{version('av')} decoded {frames} audio frames"


def register_cuda_dlls():
    """Put the pip NVIDIA libraries (nvidia/*/bin) on the DLL path, as CTranslate2 needs on Windows."""
    roots = []
    try:
        roots += site.getsitepackages()
    except Exception:
        pass
    try:
        roots.append(site.getusersitepackages())
    except Exception:
        pass
    for sp in roots:
        for d in glob.glob(os.path.join(sp, "nvidia", "*", "bin")):
            if os.path.isdir(d):
                try:
                    os.add_dll_directory(d)
                except Exception:
                    pass
                os.environ["PATH"] = d + os.pathsep + os.environ.get("PATH", "")


def cached_whisper_model():
    """The smallest faster-whisper model already downloaded, or None. Never downloads one."""
    hub = os.environ.get("HF_HUB_CACHE") or os.path.join(
        os.environ.get("HF_HOME") or os.path.join(os.path.expanduser("~"), ".cache", "huggingface"), "hub")
    for size in ["tiny", "base", "small", "medium", "large-v3", "large-v2", "large-v1", "large"]:
        for prefix in ["models--Systran--faster-whisper-", "models--guillaumekln--faster-whisper-"]:
            if glob.glob(os.path.join(hub, prefix + size, "snapshots", "*", "model.bin")):
                return size
    return None


def t_whisper():
    need("faster_whisper", "ctranslate2")
    if not wav or not os.path.isfile(wav):
        raise Skip("no test audio")
    model_name = cached_whisper_model()
    if model_name is None:
        raise Skip("no model downloaded (not fetching one)")
    register_cuda_dlls()
    import ctranslate2
    from faster_whisper import WhisperModel
    gpu = ctranslate2.get_cuda_device_count() > 0
    model = WhisperModel(model_name, device="cuda" if gpu else "cpu", compute_type="float16" if gpu else "int8",
                         local_files_only=True)
    segs, _ = model.transcribe(wav, language="en")
    text = " ".join(s.text.strip() for s in segs)
    if "fox" not in text.lower():
        raise AssertionError(f"heard {text!r}")
    return f"faster-whisper {version('faster-whisper')}, {model_name} on {'GPU' if gpu else 'CPU'}, heard={text!r}"


def t_onnx():
    need("onnxruntime")
    import onnxruntime as ort
    return f"{version('onnxruntime')} providers={ort.get_available_providers()}"


def t_playwright():
    need("playwright")
    root = os.environ.get("PLAYWRIGHT_BROWSERS_PATH") or os.path.join(os.environ.get("LOCALAPPDATA", ""), "ms-playwright")
    if not glob.glob(os.path.join(root, "chromium*")):
        raise Skip("no browser downloaded")
    from playwright.sync_api import sync_playwright
    with sync_playwright() as p:
        b = p.chromium.launch(headless=True)
        pg = b.new_page()
        pg.set_content("<title>check ok</title>")
        t = pg.title()
        b.close()
    return f"{version('playwright')} title={t!r}"


for name, fn in [("pymupdf", t_pymupdf), ("pillow", t_pillow), ("pypdf", t_pypdf), ("python-docx", t_docx),
                 ("python-pptx", t_pptx), ("excel", t_excel), ("numpy", t_numpy), ("lxml", t_lxml),
                 ("requests", t_requests), ("av", t_av), ("whisper", t_whisper), ("onnxruntime", t_onnx),
                 ("playwright", t_playwright)]:
    check(name, fn)

print(f"python {sys.version.split()[0]}")
print("\n".join(results))
print(f"SUMMARY {sum(r.startswith('PASS') for r in results)} pass, {sum(r.startswith('FAIL') for r in results)} fail, "
      f"{sum(r.startswith('SKIP') for r in results)} skipped")

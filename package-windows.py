#!/usr/bin/env python3
"""Package an already published, self-contained Windows application and setup stub."""
import argparse
import hashlib
import json
import shutil
import struct
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
VERSION = "0.2.0"
MARKER = b"GUIAFOTOSZIP-V1!"


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def files(root):
    result = sorted(p for p in root.rglob("*") if p.is_file())
    for path in root.rglob("*"):
        if path.is_symlink():
            raise ValueError(f"Link não permitido no pacote: {path}")
    return result


def manifest(root):
    target = root / "SHA256SUMS.txt"
    target.write_text("".join(f"{sha256(p)}  {p.relative_to(root).as_posix()}\n"
                              for p in files(root) if p != target), encoding="utf-8")


def verify(root):
    rows = {}
    for line in (root / "SHA256SUMS.txt").read_text(encoding="utf-8-sig").splitlines():
        digest, name = line.split("  ", 1)
        if name == "SHA256SUMS.txt" or name in rows or Path(name).is_absolute() or ".." in Path(name).parts:
            raise ValueError(f"Manifesto inválido: {name}")
        if sha256(root / name) != digest:
            raise ValueError(f"Hash divergente: {name}")
        rows[name] = digest
    actual = {p.relative_to(root).as_posix() for p in files(root)} - {"SHA256SUMS.txt"}
    if set(rows) != actual:
        raise ValueError("Inventário não cobre exatamente o pacote")
    return rows


def archive(root, target, prefix=""):
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as output:
        for path in files(root):
            output.write(path, prefix + path.relative_to(root).as_posix())
    with zipfile.ZipFile(target) as output:
        bad = output.testzip()
        if bad:
            raise ValueError(f"ZIP corrompido: {bad}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish", type=Path, default=ROOT / "artifacts/windows-x64")
    parser.add_argument("--setup-stub", type=Path, default=ROOT / "artifacts/setup-stub/GuiaFotos.Setup.exe")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts")
    args = parser.parse_args()
    publish = args.publish.resolve()
    output = args.output.resolve()
    required = ["GuiaFotos.exe", "GuiaFotos.dll", "GuiaFotos.runtimeconfig.json", "coreclr.dll",
                "PresentationFramework.dll", "tools/exiftool.exe", "tools/exiftool_files/LICENSE",
                "tools/ffmpeg.exe", "tools/ffprobe.exe", "tools/magick.exe",
                "tools/LICENSE.txt", "tools/NOTICE.txt", "tools/licenses/ffmpeg/LICENSE",
                "licenses/dotnet/core-LICENSE.txt", "licenses/dotnet/desktop-LICENSE.txt", "tools/faces/onnxruntime.dll", "tools/faces/onnxruntime-LICENSE.txt", "tools/faces/yunet-LICENSE.txt", "tools/faces/sface-LICENSE.txt", "tools/faces/adaface-LICENSE.txt", "tools/faces/adaface-export-LICENSE.txt", "tools/faces/edgeface-LICENSE.txt", "tools/faces/edgeface-export-LICENSE.txt", "tools/faces/uniface-LICENSE.txt"]
    for name in required:
        if not (publish / name).is_file():
            raise ValueError(f"Publish incompleto: {name}")
    # Check the input too: packaging must not turn stale/tampered inputs into a valid release.
    verify(publish)
    runtime = json.loads((publish / "GuiaFotos.runtimeconfig.json").read_text())["runtimeOptions"]
    if "framework" in runtime or "frameworks" in runtime:
        raise ValueError("O executável deve incluir o runtime .NET")
    if args.setup_stub.read_bytes()[:2] != b"MZ":
        raise ValueError("Stub do instalador não é um executável Windows")
    if (publish / "portable.marker").exists() or (publish / "Data").exists():
        raise ValueError("Não empacote um diretório portátil usado ou seus dados pessoais")
    sscd_dir = publish / "tools/sscd"
    sscd_included = (sscd_dir / "sscd-model.json").is_file()
    if sscd_included:
        model = json.loads((sscd_dir / "sscd-model.json").read_text())
        if model["InputSize"] != 320 or model["Dimensions"] != 512 or not 0 <= model["MaximumExportError"] <= .0001 or sha256(sscd_dir / "sscd.onnx") != model["Sha256"] or not (sscd_dir / "LICENSE.txt").is_file():
            raise ValueError("SSCD sem integridade, licença ou paridade numérica válida")
    elif (sscd_dir / "sscd.onnx").exists():
        raise ValueError("Não empacote SSCD sem seu manifesto de validação")
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="packaging-", dir=output) as temp:
        stage = Path(temp) / "payload"
        shutil.copytree(publish, stage)
        for name in ["INSTRUCOES.txt", "LICENSE.txt", "THIRD-PARTY.txt", "LEIA-ME-PACOTES.txt", "REVISAO-SENTINELA.txt"]:
            shutil.copy2(ROOT / name, stage / name)
        manifest(stage)
        installed_manifest = verify(stage)
        payload = Path(temp) / "installed.zip"
        print("Compactando conteúdo do instalador...", flush=True)
        archive(stage, payload)
        setup = Path(temp) / f"GuiaFotos-{VERSION}-Setup.exe"
        with setup.open("wb") as destination:
            with args.setup_stub.open("rb") as stub:
                shutil.copyfileobj(stub, destination)
            with payload.open("rb") as source:
                shutil.copyfileobj(source, destination)
            destination.write(struct.pack("<q", payload.stat().st_size))
            destination.write(bytes.fromhex(sha256(payload)))
            destination.write(MARKER)
        with setup.open("rb") as check:
            check.seek(-56, 2)
            length = struct.unpack("<q", check.read(8))[0]
            digest = check.read(32).hex()
            if length != payload.stat().st_size or digest != sha256(payload) or check.read(16) != MARKER:
                raise ValueError("Trailer inválido no instalador")
        (stage / "portable.marker").write_text("GuiaFotos portable 0.2.0\n", encoding="utf-8")
        manifest(stage)
        portable_manifest = verify(stage)
        portable = Path(temp) / f"GuiaFotos-{VERSION}-portatil-windows-x64.zip"
        print("Compactando versão portátil...", flush=True)
        archive(stage, portable, "GuiaFotos/")
        source_zip = Path(temp) / f"GuiaFotos-{VERSION}-fonte.zip"
        excluded = {"bin", "obj", "artifacts", "tools", "__pycache__", ".git"}
        with zipfile.ZipFile(source_zip, "w", zipfile.ZIP_DEFLATED) as archive_file:
            for path in files(ROOT):
                rel = path.relative_to(ROOT)
                if not excluded.intersection(rel.parts) and not path.name.endswith((".secret", ".user")):
                    archive_file.write(path, "GuiaFotos/" + rel.as_posix())
        products = [setup, portable, source_zip]
        report = {
            "version": VERSION,
            "installer_files": len(installed_manifest), "portable_files": len(portable_manifest),
            "windows_execution_tested": False, "facial_recognition_included": True, "pretrained_face_inference_tested": False, "face_models_download_on_activation": True,
            "selectable_face_profiles": ["adaface-ir101", "adaface-ir50", "edgeface-base", "edgeface-xxs", "sface"],
            "face_detectors": ["SCRFD 10G", "YuNet 2023mar"], "face_tiled_detection": True,
            "global_face_clustering": "constrained DBSCAN", "vector_index": "persistent exact VP-tree",
            "visual_families": True, "sscd_included": sscd_included, "sscd_export_parity_verified": sscd_included, "face_indices_isolated_by_profile": True,
            "checks": ["publish manifest", "self-contained runtime", "required tools and licenses",
                       "complete output manifests", "ZIP CRC", "installer payload SHA256 and trailer"],
            "products": [{"file": p.name, "bytes": p.stat().st_size, "sha256": sha256(p)} for p in products],
        }
        for path in products:
            path.replace(output / path.name)
        (output / f"GuiaFotos-{VERSION}-validacao-pacotes.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        (output / f"GuiaFotos-{VERSION}-SHA256SUMS.txt").write_text("".join(f"{p['sha256']}  {p['file']}\n" for p in report["products"]), encoding="utf-8")
        for product in report["products"]:
            print(f"Pronto: {output / product['file']} ({product['bytes'] / 1048576:.1f} MiB)")


if __name__ == "__main__":
    main()

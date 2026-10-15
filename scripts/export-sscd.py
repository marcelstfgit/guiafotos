#!/usr/bin/env python3
"""Build-time conversion only. End users run the exported model through ONNX CPU."""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.request

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--source', type=Path)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
manifest_path = args.output / 'sscd-model.json'
manifest_path.unlink(missing_ok=True)  # A failed parity run must never leave a valid manifest.
source = args.source or args.output / 'sscd_disc_mixup.torchscript.pt'
if not source.exists():
    urllib.request.urlretrieve('https://dl.fbaipublicfiles.com/sscd-copy-detection/sscd_disc_mixup.torchscript.pt', source)

import numpy as np
import onnxruntime as ort
import torch

torch.set_num_threads(4)
torch.manual_seed(1847)
reference = torch.jit.load(str(source), map_location='cpu').eval()
output = args.output / 'sscd.onnx'
example = torch.rand(1, 3, 320, 320)
with torch.inference_mode():
    torch.onnx.export(reference, example, str(output), input_names=['images'], output_names=['embeddings'], opset_version=17, do_constant_folding=True)
session = ort.InferenceSession(str(output), providers=['CPUExecutionProvider'])
maximum_error = 0.0
# Identical inputs on both engines: numerical export parity, independent of accuracy.
for seed in range(12):
    torch.manual_seed(seed)
    image = torch.rand(1, 3, 320, 320)
    if seed % 3 == 1:
        image = (image * .6 + .2).clamp(0, 1)
    elif seed % 3 == 2:
        image = torch.nn.functional.avg_pool2d(image, 3, stride=1, padding=1)
    mean = torch.tensor([.485, .456, .406]).reshape(1, 3, 1, 1)
    std = torch.tensor([.229, .224, .225]).reshape(1, 3, 1, 1)
    image = (image - mean) / std
    with torch.inference_mode():
        expected = reference(image).numpy()
    actual = session.run(None, {'images': image.numpy()})[0]
    assert expected.shape == actual.shape == (1, 512)
    error = float(np.max(np.abs(expected - actual)))
    assert np.isfinite(error) and error <= .0001, error
    maximum_error = max(maximum_error, error)
manifest_path.write_text(json.dumps({
    'Version': 'sscd-disc-mixup-resnet50-320-v1',
    'Sha256': hashlib.sha256(output.read_bytes()).hexdigest(),
    'InputSize': 320, 'Dimensions': 512, 'MaximumExportError': maximum_error,
    'SourceSha256': hashlib.sha256(source.read_bytes()).hexdigest(),
    'SourceUrl': 'https://dl.fbaipublicfiles.com/sscd-copy-detection/sscd_disc_mixup.torchscript.pt',
    'ParityInputs': 12, 'RealPhotoAccuracyTested': False,
}, indent=2) + '\n')
# Ship only the validated ONNX, metadata and upstream license.
if args.source is None:
    source.unlink()
print(f'PASS SSCD PyTorch/ONNX parity: 12 inputs, max_abs_error={maximum_error:.8g}')

"""Offline Piper voice worker: UTF-8 text on stdin, PCM WAV on stdout."""

import argparse
import io
import json
from pathlib import Path
import shutil
import struct
import sys
import tempfile
import wave


def espeak_data_path():
    import espeakng_loader

    source = Path(espeakng_loader.get_data_path())
    if source.as_posix().isascii():
        return str(source)
    # The Windows eSpeak DLL requires a path representable in its code page.
    target = Path(tempfile.gettempdir()) / "EtsVoiceBridge-espeak-data"
    if not (target / "phontab").is_file():
        shutil.copytree(source, target, dirs_exist_ok=True)
    return str(target)


def phoneme_ids(text, config):
    import espeakng_loader
    from phonemizer import phonemize
    from phonemizer.backend.espeak.wrapper import EspeakWrapper

    EspeakWrapper.set_library(espeakng_loader.get_library_path())
    EspeakWrapper.set_data_path(espeak_data_path())
    phonemes = phonemize(
        text, language=config["espeak"]["voice"], backend="espeak",
        strip=True, preserve_punctuation=True, with_stress=True,
    )
    mapping = config["phoneme_id_map"]
    ids = list(mapping["^"])
    for phoneme in phonemes:
        if phoneme in mapping:
            ids.extend(mapping[phoneme])
            ids.extend(mapping["_"])
    ids.extend(mapping["$"])
    if len(ids) <= 2:
        raise ValueError("No supported phonemes found in the selected text.")
    return ids


def make_wav(session, text, config):
    import numpy as np

    text = text.encode("utf-8", errors="replace").decode("utf-8")
    ids = phoneme_ids(text, config)
    inference = config["inference"]
    samples = session.run(None, {
        "input": np.asarray([ids], dtype=np.int64),
        "input_lengths": np.asarray([len(ids)], dtype=np.int64),
        "scales": np.asarray([
            inference["noise_scale"], inference["length_scale"], inference["noise_w"]
        ], dtype=np.float32),
    })[0].reshape(-1)
    pcm = (np.clip(samples, -1.0, 1.0) * 32767).astype("<i2").tobytes()
    output = io.BytesIO()
    with wave.open(output, "wb") as writer:
        writer.setnchannels(1)
        writer.setsampwidth(2)
        writer.setframerate(config["audio"]["sample_rate"])
        writer.writeframes(pcm)
    return output.getvalue()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--voices", required=True, help="Piper ONNX JSON config")
    parser.add_argument("--voice", default="en_US-lessac-medium")
    parser.add_argument("--device", type=int, default=0)
    parser.add_argument("--batch", action="store_true")
    args = parser.parse_args()

    import numpy as np
    import onnxruntime as ort

    if "DmlExecutionProvider" not in ort.get_available_providers():
        raise RuntimeError("DirectML is unavailable. Install onnxruntime-directml.")
    config = json.loads(Path(args.voices).read_text(encoding="utf-8"))
    payload = sys.stdin.buffer.read().decode("utf-8-sig", errors="replace").strip()
    if not payload:
        raise ValueError("朗读文字为空。")
    jobs = json.loads(payload) if args.batch else None
    if args.batch and (not isinstance(jobs, list) or not jobs or
                       not all(isinstance(text, str) and text.strip() for text in jobs)):
        raise ValueError("批量生成需要非空的答案列表。")

    options = ort.SessionOptions()
    options.enable_mem_pattern = False
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.log_severity_level = 4
    session = ort.InferenceSession(
        args.model, sess_options=options,
        providers=[("DmlExecutionProvider", {"device_id": str(args.device)}),
                   "CPUExecutionProvider"],
    )
    if session.get_providers()[0] != "DmlExecutionProvider":
        raise RuntimeError("DirectML was not activated.")
    if args.batch:
        for index, text in enumerate(jobs, start=1):
            wav = make_wav(session, text.strip(), config)
            sys.stdout.buffer.write(struct.pack("<Q", len(wav)))
            sys.stdout.buffer.write(wav)
            sys.stdout.buffer.flush()
            print(f"PROGRESS {index}", file=sys.stderr, flush=True)
        sys.stdout.buffer.flush()
        print("DirectML Piper batch completed.", file=sys.stderr)
    else:
        sys.stdout.buffer.write(make_wav(session, payload, config))
        sys.stdout.buffer.flush()
        print("DirectML Piper synthesis completed.", file=sys.stderr)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"{type(error).__name__}: {error}", file=sys.stderr)
        sys.exit(1)

import base64
import io
import json
import os
import sys
import traceback
import wave

_protocol_stdout = os.fdopen(
    os.dup(sys.stdout.fileno()),
    "w",
    encoding="utf-8",
    buffering=1,
)
os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
sys.stdout = sys.stderr

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"

MAX_REQUEST_CHARS = 1400
MAX_MESSAGE_CHARS = 16_000_000


def emit(message):
    line = json.dumps(message, ensure_ascii=False, separators=(",", ":"))
    if len(line) > MAX_MESSAGE_CHARS:
        raise ValueError("Worker response exceeded the protocol size limit.")
    _protocol_stdout.write(line + "\n")
    _protocol_stdout.flush()


def main():
    if len(sys.argv) != 2:
        raise RuntimeError("The Qwen worker requires one installed model directory.")

    import numpy as np
    import torch
    from qwen_tts import Qwen3TTSModel

    if not torch.cuda.is_available():
        raise RuntimeError(
            "Qwen3-TTS requires a working NVIDIA CUDA runtime. "
            "Check the NVIDIA driver and install the pinned CUDA-enabled PyTorch runtime again."
        )

    model = Qwen3TTSModel.from_pretrained(
        sys.argv[1],
        device_map="cuda:0",
        dtype=torch.bfloat16,
        attn_implementation="sdpa",
        local_files_only=True,
    )
    voices = model.get_supported_speakers()
    languages = model.get_supported_languages()
    emit(
        {
            "type": "ready",
            "gpu": torch.cuda.get_device_name(0),
            "voices": voices,
            "languages": languages,
        }
    )

    for line in sys.stdin:
        try:
            if len(line) > MAX_MESSAGE_CHARS:
                raise ValueError("Request exceeded the protocol size limit.")
            request = json.loads(line)
            if request.get("command") == "close":
                emit({"type": "closed"})
                return

            request_id = request.get("requestId")
            text = request.get("text")
            voice = request.get("voiceId")
            language = request.get("language")
            instruction = request.get("instruction", "")
            if (
                not isinstance(text, str)
                or not text.strip()
                or len(text) > MAX_REQUEST_CHARS
                or not isinstance(voice, str)
                or not isinstance(language, str)
                or not isinstance(instruction, str)
                or len(instruction) > 500
            ):
                raise ValueError("Request text, voice, language, or instruction is invalid.")

            speaker_names = {item.casefold(): item for item in voices}
            language_names = {item.casefold(): item for item in languages}
            if voice.casefold() not in speaker_names or language.casefold() not in language_names:
                raise ValueError("The requested Qwen voice or language is not supported.")

            wavs, sample_rate = model.generate_custom_voice(
                text=text,
                language=language_names[language.casefold()],
                speaker=speaker_names[voice.casefold()],
                instruct=instruction or None,
            )
            samples = np.asarray(wavs[0], dtype=np.float32)
            if (
                sample_rate <= 0
                or samples.size == 0
                or not np.isfinite(samples).all()
            ):
                raise RuntimeError("Qwen returned invalid audio samples.")
            pcm = (np.clip(samples, -1.0, 1.0) * 32767).astype("<i2")
            output = io.BytesIO()
            with wave.open(output, "wb") as wav:
                wav.setnchannels(1)
                wav.setsampwidth(2)
                wav.setframerate(sample_rate)
                wav.writeframes(pcm.tobytes())
            emit(
                {
                    "type": "audio",
                    "requestId": request_id,
                    "sampleRate": sample_rate,
                    "durationSeconds": pcm.size / sample_rate,
                    "wavBase64": base64.b64encode(output.getvalue()).decode("ascii"),
                }
            )
        except Exception as error:
            emit(
                {
                    "type": "error",
                    "requestId": locals().get("request_id"),
                    "message": str(error),
                    "traceback": traceback.format_exc(limit=5),
                }
            )


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit({"type": "startupError", "message": str(error), "traceback": traceback.format_exc(limit=8)})
        sys.exit(1)

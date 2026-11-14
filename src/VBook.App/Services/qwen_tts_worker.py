import base64
import gc
import hashlib
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

MAX_REQUEST_CHARS = 700
MAX_MESSAGE_CHARS = 16_000_000
MAX_INSTRUCTION_CHARS = 500
SUPPORTED_LANGUAGES = {
    "chinese",
    "english",
    "french",
    "german",
    "italian",
    "japanese",
    "korean",
    "portuguese",
    "russian",
    "spanish",
}


def emit(message):
    line = json.dumps(message, ensure_ascii=False, separators=(",", ":"))
    if len(line) > MAX_MESSAGE_CHARS:
        raise ValueError("Worker response exceeded the protocol size limit.")
    _protocol_stdout.write(line + "\n")
    _protocol_stdout.flush()


def make_wav(samples, sample_rate, np):
    samples = np.asarray(samples, dtype=np.float32)
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
    return output.getvalue(), pcm.size / sample_rate


def main():
    if len(sys.argv) != 3:
        raise RuntimeError(
            "The Qwen worker requires VoiceDesign and Base model directories."
        )

    import numpy as np
    import torch
    from qwen_tts import Qwen3TTSModel

    if not torch.cuda.is_available():
        raise RuntimeError(
            "Qwen3-TTS requires a working NVIDIA CUDA runtime. "
            "Check the NVIDIA driver and install the pinned CUDA-enabled PyTorch runtime again."
        )

    model_directories = {
        "design": sys.argv[1],
        "base": sys.argv[2],
    }
    model = None
    active_model = None
    voice_clone_prompts = {}

    def load_model(model_type):
        nonlocal model, active_model, voice_clone_prompts
        if active_model == model_type:
            return model
        if model is not None:
            model = None
            active_model = None
            voice_clone_prompts.clear()
            gc.collect()
            torch.cuda.empty_cache()
        model = Qwen3TTSModel.from_pretrained(
            model_directories[model_type],
            device_map="cuda:0",
            dtype=torch.bfloat16,
            attn_implementation="sdpa",
            local_files_only=True,
        )
        active_model = model_type
        return model

    emit({"type": "ready", "gpu": torch.cuda.get_device_name(0)})

    for line in sys.stdin:
        request_id = None
        try:
            if len(line) > MAX_MESSAGE_CHARS:
                raise ValueError("Request exceeded the protocol size limit.")
            request = json.loads(line)
            command = request.get("command")
            if command == "close":
                emit({"type": "closed"})
                return

            request_id = request.get("requestId")
            if not isinstance(request_id, str) or not request_id:
                raise ValueError("Request id is required.")

            if command == "designVoice":
                text = request.get("text")
                language = request.get("language")
                instruction = request.get("instruction", "")
                validate_text_request(text, language, instruction)
                design_model = load_model("design")
                wavs, sample_rate = design_model.generate_voice_design(
                    text=text,
                    language=language,
                    instruct=instruction or None,
                )
                wav_bytes, duration = make_wav(wavs[0], sample_rate, np)
                emit(
                    {
                        "type": "audio",
                        "requestId": request_id,
                        "durationSeconds": duration,
                        "wavBase64": base64.b64encode(wav_bytes).decode("ascii"),
                    }
                )
            elif command == "loadVoiceClonePrompt":
                prompt_id = request.get("promptId")
                reference_text = request.get("referenceText")
                reference_audio = base64.b64decode(
                    request.get("referenceAudioBase64", ""),
                    validate=True,
                )
                if (
                    not isinstance(prompt_id, str)
                    or not prompt_id
                    or not isinstance(reference_text, str)
                    or not reference_text.strip()
                    or not reference_audio
                ):
                    raise ValueError("Voice-clone reference audio, text, and id are required.")

                base_model = load_model("base")
                with wave.open(io.BytesIO(reference_audio), "rb") as wav:
                    if wav.getcomptype() != "NONE" or wav.getsampwidth() != 2:
                        raise ValueError("Voice-clone reference audio must be uncompressed 16-bit PCM.")
                    if wav.getnchannels() != 1:
                        raise ValueError("Voice-clone reference audio must be mono.")
                    sample_rate = wav.getframerate()
                    samples = np.frombuffer(wav.readframes(wav.getnframes()), dtype="<i2")
                if sample_rate <= 0 or samples.size == 0:
                    raise ValueError("Voice-clone reference audio is empty or invalid.")

                voice_clone_prompts[prompt_id] = base_model.create_voice_clone_prompt(
                    ref_audio=(samples.astype(np.float32) / 32768.0, sample_rate),
                    ref_text=reference_text,
                )
                emit(
                    {
                        "type": "promptLoaded",
                        "requestId": request_id,
                        "promptId": prompt_id,
                    }
                )
            elif command == "synthesize":
                text = request.get("text")
                voice = request.get("voiceId")
                language = request.get("language")
                prompt_id = request.get("voiceClonePromptId")
                validate_text_request(text, language, "")
                if voice != "Designed":
                    raise ValueError("Qwen VoiceDesign profiles must use the Designed voice id.")
                if not isinstance(prompt_id, str) or prompt_id not in voice_clone_prompts:
                    raise ValueError("The Qwen voice-clone prompt is not loaded.")

                base_model = load_model("base")
                wavs, sample_rate = base_model.generate_voice_clone(
                    text=text,
                    language=language,
                    voice_clone_prompt=voice_clone_prompts[prompt_id],
                )
                wav_bytes, duration = make_wav(wavs[0], sample_rate, np)
                emit(
                    {
                        "type": "audio",
                        "requestId": request_id,
                        "durationSeconds": duration,
                        "wavBase64": base64.b64encode(wav_bytes).decode("ascii"),
                    }
                )
            else:
                raise ValueError(f"Unknown Qwen command '{command}'.")
        except Exception as error:
            emit(
                {
                    "type": "error",
                    "requestId": request_id,
                    "message": str(error),
                    "traceback": traceback.format_exc(limit=5),
                }
            )


def validate_text_request(text, language, instruction):
    if (
        not isinstance(text, str)
        or not text.strip()
        or len(text) > MAX_REQUEST_CHARS
        or not isinstance(language, str)
        or language.casefold() not in SUPPORTED_LANGUAGES
        or not isinstance(instruction, str)
        or len(instruction) > MAX_INSTRUCTION_CHARS
    ):
        raise ValueError("Request text, language, or voice description is invalid.")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit({"type": "startupError", "message": str(error), "traceback": traceback.format_exc(limit=8)})
        sys.exit(1)

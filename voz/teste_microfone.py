from vosk import Model, KaldiRecognizer
import sounddevice as sd
import json

import os
model_path = os.path.join(os.path.dirname(__file__), "vosk-model-small-pt-0.3")
model = Model(model_path)
samplerate = 16000
duration = 5

rec = KaldiRecognizer(model, samplerate)
print("Ouvindo...")

def callback(indata, frames, time, status):
    # Garante que indata seja convertido para bytes corretamente
    try:
        data_bytes = indata.tobytes()
    except AttributeError:
        # Caso seja um buffer do cffi
        import numpy as np
        data_bytes = np.frombuffer(indata, dtype=np.float32).tobytes()
    rec.AcceptWaveform(data_bytes)

with sd.RawInputStream(samplerate=samplerate, blocksize=8000, dtype='int16', channels=1, callback=callback):
    sd.sleep(int(duration * 1000))
result = rec.Result()
print(json.loads(result).get("text", ""))

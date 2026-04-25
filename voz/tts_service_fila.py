from fastapi import FastAPI
from pydantic import BaseModel
import pyttsx3
import threading
import queue

app = FastAPI()

class TTSRequest(BaseModel):
    text: str

# Fila para requisições de fala
tts_queue = queue.Queue()

# Worker que processa a fila

def tts_worker():
    engine = pyttsx3.init()
    def set_voice():
        for voice in engine.getProperty('voices'):
            if 'brazil' in voice.name.lower() or 'portuguese' in voice.name.lower():
                if 'female' in voice.name.lower() or 'feminina' in voice.name.lower():
                    engine.setProperty('voice', voice.id)
                    break
        else:
            for voice in engine.getProperty('voices'):
                if 'portuguese' in voice.name.lower():
                    engine.setProperty('voice', voice.id)
                    break
    set_voice()
    engine.setProperty('rate', 180)
    while True:
        text = tts_queue.get()
        if text is None:
            break
        engine.say(text)
        engine.runAndWait()
        tts_queue.task_done()

threading.Thread(target=tts_worker, daemon=True).start()

@app.post("/speak")
def speak(req: TTSRequest):
    tts_queue.put(req.text)
    return {"status": "queued"}

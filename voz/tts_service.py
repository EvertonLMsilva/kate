from fastapi import FastAPI
from pydantic import BaseModel
import pyttsx3
import threading

app = FastAPI()
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

class TTSRequest(BaseModel):
    text: str

def speak_async(text):
    engine.say(text)
    engine.runAndWait()

@app.post("/speak")
def speak(req: TTSRequest):
    threading.Thread(target=speak_async, args=(req.text,)).start()
    return {"status": "ok"}

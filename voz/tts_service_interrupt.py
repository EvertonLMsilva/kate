from fastapi import FastAPI
from pydantic import BaseModel
import pyttsx3
import threading

app = FastAPI()

class TTSRequest(BaseModel):
    text: str

# Variável global para controle de interrupção
tts_lock = threading.Lock()
current_engine = None

@app.post("/speak")
def speak(req: TTSRequest):
    global current_engine
    with tts_lock:
        # Se já estiver narrando, pare imediatamente
        if current_engine is not None:
            try:
                current_engine.stop()
            except Exception:
                pass
        # Cria novo engine para a nova fala
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
        current_engine = engine
    def speak_and_clear():
        engine.say(req.text)
        engine.runAndWait()
        with tts_lock:
            global current_engine
            if current_engine == engine:
                current_engine = None
    threading.Thread(target=speak_and_clear, daemon=True).start()
    return {"status": "speaking"}

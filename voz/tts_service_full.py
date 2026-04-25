from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.responses import JSONResponse
from pydantic import BaseModel
import threading
import queue
import speech_recognition as sr
import pyttsx3
from vosk import Model, KaldiRecognizer
import sounddevice as sd
import json
import os

app = FastAPI()

# Configuração global do dispositivo de saída
selected_output_device = None

class OutputDeviceRequest(BaseModel):
    device_index: int | None = None
    device_name: str | None = None

@app.post("/set_output_device")
def set_output_device(req: OutputDeviceRequest):
    global selected_output_device
    if req.device_index is not None:
        selected_output_device = req.device_index
    elif req.device_name:
        selected_output_device = req.device_name
    else:
        selected_output_device = None
    return {"status": "ok", "selected_output_device": selected_output_device}

# Configuração global do dispositivo de entrada
selected_input_device = None

# Endpoint para listar dispositivos de áudio
@app.get("/audio_devices")
def list_audio_devices():
    devices = sd.query_devices()
    return JSONResponse(content={"devices": devices})

# Endpoint para definir o dispositivo de entrada

class DeviceRequest(BaseModel):
    device_index: int | None = None
    device_name: str | None = None

@app.post("/set_input_device")
def set_input_device(req: DeviceRequest):
    global selected_input_device
    # Prioriza o índice, pois é único. Se não houver índice, usa o nome.
    if req.device_index is not None:
        selected_input_device = req.device_index
    elif req.device_name:
        selected_input_device = req.device_name
    else:
        selected_input_device = None
    return {"status": "ok", "selected_input_device": selected_input_device}

# ========== TTS (Texto para fala) ==========
class TTSRequest(BaseModel):
    text: str

tts_lock = threading.Lock()
current_engine = None

@app.post("/speak")
def speak(req: TTSRequest):
    global current_engine
    with tts_lock:
        if current_engine is not None:
            try:
                current_engine.stop()
            except Exception:
                pass
        engine = pyttsx3.init()
        def set_voice():
            # Prioriza pt-BR (Brasil)
            for voice in engine.getProperty('voices'):
                if ('brazil' in voice.name.lower() or 'brazil' in voice.id.lower() or 'pt-br' in voice.id.lower()) and ('female' in voice.name.lower() or 'feminina' in voice.name.lower()):
                    engine.setProperty('voice', voice.id)
                    return
            for voice in engine.getProperty('voices'):
                if 'brazil' in voice.name.lower() or 'brazil' in voice.id.lower() or 'pt-br' in voice.id.lower():
                    engine.setProperty('voice', voice.id)
                    return
            for voice in engine.getProperty('voices'):
                if 'portuguese' in voice.name.lower() or 'portuguese' in voice.id.lower():
                    engine.setProperty('voice', voice.id)
                    return
            # fallback: primeira voz
            voices = engine.getProperty('voices')
            if voices:
                engine.setProperty('voice', voices[0].id)
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

# ========== STT (Fala para texto) ==========

@app.post("/listen")
def listen():
    model_path = os.path.join(os.path.dirname(__file__), "vosk-model-small-pt-0.3")
    if not os.path.exists(model_path):
        return {"error": f"Modelo Vosk não encontrado em {model_path}"}
    model = Model(model_path)
    samplerate = 16000
    rec = KaldiRecognizer(model, samplerate)
    print("Ouvindo...")
    silence_threshold = 200  # Amplitude mínima para considerar como voz (mais sensível)
    silence_max_time = 1.2   # Segundos de silêncio para parar (um pouco mais tolerante)
    max_time = 15            # Limite máximo de escuta (segundos)
    last_voice_time = None
    import time as time_mod
    start_time = time_mod.time()
    def callback(indata, frames, time, status):
        nonlocal last_voice_time
        try:
            data_bytes = indata.tobytes()
        except AttributeError:
            import numpy as np
            data_bytes = np.frombuffer(indata, dtype=np.int16).tobytes()
        rec.AcceptWaveform(data_bytes)
        # Detecção de silêncio
        amplitude = max(abs(int.from_bytes(data_bytes[i:i+2], 'little', signed=True)) for i in range(0, len(data_bytes), 2))
        if amplitude > silence_threshold:
            last_voice_time = time_mod.time()
    global selected_input_device
    stream_kwargs = dict(samplerate=samplerate, blocksize=8000, dtype='int16', channels=1, callback=callback)
    if selected_input_device is not None:
        stream_kwargs['device'] = selected_input_device
    print("Usando device:", stream_kwargs.get('device', 'default'))
    last_voice_time = time_mod.time()
    with sd.RawInputStream(**stream_kwargs):
        while True:
            sd.sleep(200)  # 200ms
            now = time_mod.time()
            if now - last_voice_time > silence_max_time:
                break
            if now - start_time > max_time:
                break
    result = rec.Result()
    text = json.loads(result).get("text", "")
    print("Texto reconhecido:", text)
    return {"text": text}

# ========== WebSocket para STT contínuo ==========
@app.websocket("/ws/listen")
async def websocket_listen(websocket: WebSocket):
    await websocket.accept()
    model_path = os.path.join(os.path.dirname(__file__), "vosk-model-small-pt-0.3")
    if not os.path.exists(model_path):
        await websocket.send_json({"error": f"Modelo Vosk não encontrado em {model_path}"})
        await websocket.close()
        return
    model = Model(model_path)
    samplerate = 16000
    def new_recognizer():
        return KaldiRecognizer(model, samplerate)
    rec = new_recognizer()

    def escutar_frase(stream, silence_threshold=200, silence_max_time=1.2, max_time=10):
        """Escuta uma frase até silêncio e retorna o texto reconhecido."""
        import time as time_mod
        rec_frase = new_recognizer()
        buffer = b""
        last_voice_time = time_mod.time()
        start_time = time_mod.time()
        while True:
            data, _ = stream.read(8000)
            try:
                data_bytes = data.tobytes()
            except AttributeError:
                data_bytes = bytes(data)
            buffer += data_bytes
            amplitude = max(abs(int.from_bytes(data_bytes[i:i+2], 'little', signed=True)) for i in range(0, len(data_bytes), 2))
            if amplitude > silence_threshold:
                last_voice_time = time_mod.time()
            if len(buffer) >= samplerate:
                rec_frase.AcceptWaveform(buffer)
                buffer = b""
            now = time_mod.time()
            if now - last_voice_time > silence_max_time:
                break
            if now - start_time > max_time:
                break
        result = rec_frase.Result()
        text = json.loads(result).get("text", "")
        return text
    import numpy as np
    import time as time_mod
    global selected_input_device
    stream_kwargs = dict(samplerate=samplerate, blocksize=8000, dtype='int16', channels=1)
    if selected_input_device is not None:
        stream_kwargs['device'] = selected_input_device
    # print("[WebSocket] Ouvindo... Usando device:", stream_kwargs.get('device', 'default'))
    try:
        with sd.RawInputStream(**stream_kwargs) as stream:
            buffer = b""
            last_voice_time = time_mod.time()
            silence_threshold = 200
            silence_max_time = 1.2
            wake_words = ["você", "voce", "vose", "você!", "você.", "você?", "voce!", "voce.", "voce?"]
            while True:
                data, _ = stream.read(8000)
                try:
                    data_bytes = data.tobytes()
                except AttributeError:
                    data_bytes = bytes(data)
                buffer += data_bytes
                amplitude = max(abs(int.from_bytes(data_bytes[i:i+2], 'little', signed=True)) for i in range(0, len(data_bytes), 2))
                if amplitude > silence_threshold:
                    last_voice_time = time_mod.time()
                if len(buffer) >= samplerate:
                    if rec.AcceptWaveform(buffer):
                        result = rec.Result()
                        text = json.loads(result).get("text", "")
                        print(text)
                        lower_text = text.lower() if text else ""
                        found_wake = None
                        for w in wake_words:
                            if w in lower_text:
                                found_wake = w
                                break
                        if text and found_wake:
                            # Após ativação, escuta só a próxima frase
                            await websocket.send_json({"text": text})  # envia ativação para debug/front
                            frase = escutar_frase(stream)
                            print(frase)
                            if frase:
                                await websocket.send_json({"text": frase})
                            # Reinicializa o reconhecedor para próxima ativação
                            rec = new_recognizer()
                    buffer = b""
                # Se ficar em silêncio por muito tempo, pode encerrar (opcional)
                if time_mod.time() - last_voice_time > 60:
                    await websocket.send_json({"info": "Encerrando por inatividade."})
                    print("[WebSocket] Encerrando por inatividade.")
                    break
    except Exception as e:
        print(f"[WebSocket][ERRO] {e}")
        await websocket.send_json({"error": str(e)})
        await websocket.close()

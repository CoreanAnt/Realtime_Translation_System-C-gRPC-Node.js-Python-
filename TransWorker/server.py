import os
import sys
import asyncio
import logging
import grpc
import json
import time
import numpy as np
import soundfile as sf
import torch
import edge_tts 
import subprocess 

from transformers import AutoModelForCausalLM, AutoTokenizer
from funasr import AutoModel
import Trans_pb2
import Trans_pb2_grpc

STT_MODEL_NAME = "iic/SenseVoiceSmall"
LLM_MODEL_NAME = "Qwen/Qwen2.5-7B-Instruct" 
TTS_VOICE = "en-US-ChristopherNeural"

print(f"[시스템] 초기화 중...")

# 1. SenseVoice
print(f"[1/3] STT 모델 로딩... (SenseVoice)")
try:
    stt_model = AutoModel(
        model=STT_MODEL_NAME,
        vad_model="fsmn-vad",
        punc_model="ct-punc",
        device="cuda",
        disable_update=True
    )
except Exception as e:
    print(f"STT 모델 로딩 실패: {e}")
    sys.exit(1)

# 2. Qwen
print(f"[2/3] LLM 모델 로딩... (Qwen 7B)")
try:
    llm_tokenizer = AutoTokenizer.from_pretrained(LLM_MODEL_NAME)
    llm_model = AutoModelForCausalLM.from_pretrained(
        LLM_MODEL_NAME,
        torch_dtype=torch.bfloat16,
        device_map="auto"
    )
except Exception as e:
    print(f"LLM 모델 로딩 실패: {e}")
    sys.exit(1)

print(f"[3/3] TTS 및 FFmpeg 준비 확인")
try:
    subprocess.run(["ffmpeg", "-version"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    print("FFmpeg 감지됨")
except FileNotFoundError:
    print("[경고] ffmpeg.exe 없음. 오디오 변환 실패 가능성 있음.")

print(f"[시스템] 문맥 인식(Context-Aware) 모드 준비 완료!")

class AiWorker(Trans_pb2_grpc.AiWorkerServiceServicer):
    
    def __init__(self):
        # [기억 저장소] 최근 3개의 문장을 기억함
        self.context_history = [] 

    async def ProcessTranslation(self, request_iterator, context):
        print(f"[연결] 강사 연결됨.")
        self.context_history = [] # 새 연결마다 기억 초기화
        
        audio_buffer = np.array([], dtype=np.float32)
        
        # 번역 초 설정
        SILENCE_THRESHOLD = 0.015  
        PAUSE_LIMIT = 0.5          
        MAX_RECORD_TIME = 3.5      
        
        last_speech_time = None
        is_recording = False
        
        os.makedirs("temp_audio", exist_ok=True)

        async for chunk in request_iterator:
            raw_data = np.frombuffer(chunk.pcm_data, dtype=np.int16)
            float_data = raw_data.astype(np.float32) / 32768.0
            float_data = float_data * 2.0 
            
            rms = np.sqrt(np.mean(float_data**2))
            current_time = time.time()
            
            if rms > SILENCE_THRESHOLD:
                if not is_recording:
                    print("[듣는 중...]", end="\r")
                    is_recording = True
                last_speech_time = current_time
                audio_buffer = np.concatenate((audio_buffer, float_data))
                
            else:
                if is_recording:
                    audio_buffer = np.concatenate((audio_buffer, float_data))
                    silence_duration = current_time - last_speech_time
                    buffer_duration = len(audio_buffer) / 16000.0
                    
                    if silence_duration > PAUSE_LIMIT:
                        if buffer_duration > 0.5:
                            print(f"\n[문장 끊김] {buffer_duration:.1f}초 -> 즉시 번역")
                            async for msg in self.Pipeline(audio_buffer):
                                yield msg
                        audio_buffer = np.array([], dtype=np.float32)
                        is_recording = False

                    elif buffer_duration > MAX_RECORD_TIME:
                        print(f"\n[동시 통역] 3.5초 경과 -> 강제 번역")
                        async for msg in self.Pipeline(audio_buffer):
                            yield msg
                        audio_buffer = np.array([], dtype=np.float32)
                        is_recording = False
                        last_speech_time = current_time

    async def Pipeline(self, audio_data):
        temp_filename = f"temp_audio/chunk_{int(time.time()*1000)}.wav"
        tts_mp3 = f"temp_audio/tts_{int(time.time()*1000)}.mp3"
        tts_pcm = f"temp_audio/tts_{int(time.time()*1000)}.pcm"
        
        sf.write(temp_filename, audio_data, 16000)
        
        try:
            # 1. STT
            res = stt_model.generate(
                input=temp_filename,
                cache={},
                language="ko", 
                use_itn=True,
                batch_size_s=60,
            )
            text_ko = res[0]["text"]
            clean_text_ko = text_ko.replace("<|ko|>", "").replace("<|emo_unknown|>", "").strip()

            if clean_text_ko:
                print(f"[한국어]: {clean_text_ko}")

                # === [핵심 수정] 문맥(Context) 만들기 ===
                # 이전 대화 2~3줄을 가져와서 프롬프트에 넣어줍니다.
                recent_context = " ".join(self.context_history[-3:]) 
                
                system_prompt = "You are a professional simultaneous interpreter. Translate the 'Current Input' based on the 'Previous Context'. Output only valid JSON."
                
                # 프롬프트에 '이전 내용'을 참고하라고 명시
                user_prompt = f"""
                Previous Context: "{recent_context}"
                
                Current Input (Korean): "{clean_text_ko}"
                
                Translate Current Input into English(en), Chinese(cn), Japanese(jp), Vietnamese(vi), Thai(th).
                JSON Output format: {{ "en": "...", "cn": "...", "jp": "...", "vi": "...", "th": "..." }}
                """
                
                messages = [{"role": "system", "content": system_prompt}, {"role": "user", "content": user_prompt}]
                
                text = llm_tokenizer.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
                model_inputs = llm_tokenizer([text], return_tensors="pt").to("cuda")
                
                generated_ids = llm_model.generate(
                    model_inputs.input_ids,
                    max_new_tokens=256,
                    temperature=0.1,
                    do_sample=False
                )
                
                generated_ids = [output_ids[len(input_ids):] for input_ids, output_ids in zip(model_inputs.input_ids, generated_ids)]
                response_json_str = llm_tokenizer.batch_decode(generated_ids, skip_special_tokens=True)[0]
                
                # === [핵심 수정] 기억 저장 ===
                # 이번 문장을 역사에 기록함 (다음 번역 때 쓰려고)
                self.context_history.append(clean_text_ko)
                if len(self.context_history) > 5: # 너무 길어지면 오래된 건 삭제
                    self.context_history.pop(0)

                try:
                    # JSON 파싱
                    json_str_clean = response_json_str.replace("```json", "").replace("```", "").strip()
                    data = json.loads(json_str_clean)
                    
                    # 3. TTS
                    english_text = data.get('en', '')
                    final_audio_data = b''

                    if english_text:
                        print(f"[번역] EN: {english_text}")
                        
                        communicate = edge_tts.Communicate(english_text, TTS_VOICE)
                        await communicate.save(tts_mp3)
                        
                        subprocess.run([
                            "ffmpeg", "-y", "-i", tts_mp3,
                            "-f", "s16le", "-ac", "1", "-ar", "24000", 
                            tts_pcm
                        ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

                        if os.path.exists(tts_pcm):
                            with open(tts_pcm, "rb") as f:
                                final_audio_data = f.read()
                        
                        if os.path.exists(tts_mp3): os.remove(tts_mp3)
                        if os.path.exists(tts_pcm): os.remove(tts_pcm)
                    
                    yield Trans_pb2.BroadcastMessage(
                        audio_data=final_audio_data,
                        subtitle=json_str_clean,
                        language_code="JSON",       
                        timestamp=int(time.time() * 1000)
                    )
                    
                except json.JSONDecodeError:
                    print(f"[JSON 파싱 실패] {response_json_str}")

            else:
                pass 

        except Exception as e:
            print(f"에러: {e}")

        if os.path.exists(temp_filename):
            os.remove(temp_filename)

async def serve():
    server = grpc.aio.server()
    Trans_pb2_grpc.add_AiWorkerServiceServicer_to_server(AiWorker(), server)
    listen_addr = '[::]:50051'
    server.add_insecure_port(listen_addr)
    logging.info(f"Worker started on {listen_addr}")
    print(f">>> [시스템] AI Worker 대기 중... (Port: 50051)")
    await server.start()
    await server.wait_for_termination()

if __name__ == '__main__':
    logging.basicConfig(level=logging.INFO)
    if os.name == 'nt':
        asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())
    try:
        asyncio.run(serve())
    except KeyboardInterrupt:
        pass
Tudo sobre IA generativa local: modelos, instalação, integração e exemplos.

Transcricao local de comandos:

- A Kate continua usando Vosk para a palavra de ativacao e pode usar Whisper local apenas para refinar o comando apos a fala.
- Se os assets do Whisper nao existirem, o fluxo segue automaticamente so com o Vosk atual.
- A wake word pode ser configurada pela variavel de ambiente KATE_WAKE_WORDS.
- Formato aceito para KATE_WAKE_WORDS: termos separados por virgula, ponto e virgula ou barra vertical.
- Exemplo: KATE_WAKE_WORDS=kate,keiti,kayte
- Se a variavel nao existir, o padrao atual e: kate, keiti, kayte.

Estrutura esperada para copia automatica no desktop:

- ia_local/whisper/whisper-cli.exe ou ia_local/whisper/main.exe
- ia_local/whisper/models/*.bin ou ia_local/whisper/*.bin

Variaveis de ambiente opcionais:

- KATE_WHISPER_EXE: caminho completo para o executavel do Whisper local
- KATE_WHISPER_MODEL: caminho completo para o modelo .bin

Observacao:

- O modo hibrido melhora a transcricao do comando sem mexer na deteccao rapida da palavra master.

# Voz Local da Kate

Este projeto pode ter narracao propria em portugues sem depender de vozes instaladas no Windows.

## Caminho recomendado

Use Piper empacotado dentro do proprio repositorio.

Estrutura esperada:

```text
voz/
  piper/
    piper.exe
    libespeak-ng.dll
    onnxruntime.dll
    piper_phonemize.dll
    pt_BR-kate-medium.onnx
    pt_BR-kate-medium.onnx.json
```

Observacoes:

- O nome do modelo pode variar. O codigo procura qualquer arquivo `.onnx` em `voz/piper`.
- Modelos com `pt-br`, `pt_br`, `portuguese` ou `brazil` no caminho recebem prioridade.
- O projeto copia automaticamente a pasta `voz/piper` para o output em [model_kate/PresentationWpf/model_kate.PresentationWpf.csproj](model_kate/PresentationWpf/model_kate.PresentationWpf.csproj).

## Comportamento atual do projeto

- Primeiro tenta usar Piper empacotado no repositorio.
- Se nao encontrar Piper no projeto, pode usar `KATE_PIPER_EXE` e `KATE_PIPER_MODEL` como override opcional.
- Se nao houver narrador local em portugues, a narracao fica desativada para nao cair em voz inglesa do Windows.

## O que colocar aqui

Para um pacote portatil completo, adicione:

1. `piper.exe`
2. DLLs distribuidas junto do Piper para Windows
3. um modelo pt-BR `.onnx`
4. o arquivo de configuracao `.onnx.json` do mesmo modelo

## Resultado esperado

Com esses arquivos dentro de `voz/piper`, a Kate passa a narrar em portugues usando assets do proprio projeto, sem depender de instalacoes da maquina.
# Estimativa de Custos - Integração com IA de Terceiros (MVP Kate)

## 1. Principais Provedores de IA
- OpenAI (GPT-4, Copilot)
- Microsoft Azure OpenAI
- Outros (Google, AWS, etc.)

## 2. Estrutura de Custos
- Cobrança geralmente por quantidade de tokens (palavras processadas)
- Pode haver custos mínimos mensais, taxas de uso, ou planos pré-pagos

## 3. Exemplo de Cálculo (GPT-4/OpenAI)
- Preço médio GPT-4 (abril/2026):
  - Entrada: US$ 0,03 por 1.000 tokens
  - Saída: US$ 0,06 por 1.000 tokens
- 1.000 tokens ≈ 750 palavras

### Simulação de Uso Mensal
- Supondo 100 interações/dia, cada uma com 200 tokens de entrada e 200 de saída:
  - 100 x 30 dias = 3.000 interações/mês
  - 3.000 x 200 = 600.000 tokens entrada (US$ 18)
  - 3.000 x 200 = 600.000 tokens saída (US$ 36)
  - Total estimado: US$ 54/mês

## 4. Custos Adicionais
- Reconhecimento de voz (Microsoft Speech SDK):
  - Gratuito até certo limite, depois cobrado por hora de áudio processado
- Infraestrutura (servidor, armazenamento):
  - Pode ser necessário se for rodar backend próprio

## 5. Considerações
- Custos podem variar conforme volume de uso e provedor
- Para MVP, custos baixos se uso for moderado
- Importante monitorar consumo para evitar surpresas
- Futuramente, migrar para IA própria elimina custos recorrentes

---

Esta estimativa serve como referência para planejamento financeiro do MVP integrado com IA de terceiros.

# Visão, coleta e treinamento

Este documento descreve a arquitetura visual atual do FH6 Open Assist e o contrato para evoluir o modelo de posição do Farm de CR sem transformar capturas locais em conteúdo público.

## Divisão de responsabilidades

O projeto usa a ferramenta mais simples que consiga resolver cada classe de problema com segurança:

| Problema | Implementação | Regra |
|---|---|---|
| Captura da janela | Windows Graphics Capture + D3D11 | Somente por HWND e nos checkpoints; o jogo não pode estar minimizado |
| Texto, saldos e menus variáveis | `Windows.Media.Ocr` em processo | Idiomas do perfil do Windows; pt-BR deve estar instalado/priorizado; tela inteira ou ROI |
| Menus e diálogos de layout estável | `ClassicalGameStateDetector` | Geometria e proporções de cor determinísticas |
| Contexto de jogo | `GameContextDetector` | Combina OCR e visão clássica do mesmo frame; conflito vira `Unknown` |
| Resultado e HUD do Farm de SP | `SpRaceVision` | OCR de ROIs + cores do mesmo frame, seguido por consenso temporal conservador |
| Seleção do carro requisito | `RequiredCarSelector` | OCR, tokens posicionais e contornos clássicos; fabricante, cartão e classe precisam permanecer coerentes |
| Alinhamento variável entre placas | `CrPositionClassifier` | ONNX em CPU, com confirmação temporal conservadora |
| Auditoria offline de parte das regras clássicas | `tools/vision/analyze_classical_states.py` | OpenCV, cobertura parcial; nunca é carregado pelo executável |

Os templates PNG de `Assets/Vision` são referências herdadas para calibração offline. O runtime C# atual não executa template matching com eles.

## OCR escalado, consenso temporal e seleção de carros

`GameVisionService` pode recortar uma ROI e ampliá-la em memória antes do OCR. A escala solicitada nunca reduz a região abaixo de um pixel e é limitada para que nenhuma dimensão ultrapasse 2.400 pixels. O resize usa vizinho mais próximo, adequado aos textos pequenos da interface. Quando uma decisão também depende da tela inteira, `AnalyzeScreenWithScaledRegionsAsync` deriva o OCR global, os crops ampliados e a visão clássica do mesmo bitmap; não se deve combinar evidências capturadas em momentos diferentes para autorizar uma ação.

Além das linhas, `WindowsOcrService` expõe tokens com texto e geometria. Isso permite associar palavras a células conhecidas da interface sem clicar diretamente na posição fornecida pelo OCR. A geometria continua sendo evidência, não uma autorização isolada: regiões fora do frame são rejeitadas ou limitadas, e texto ausente ou conflitante permanece inconclusivo.

`SpRaceVision` separa três estados: `Success`, `Failure` e `Unknown`. O checkpoint combina:

- OCR ampliado do título e das ações do rodapé;
- presença de Continuar, Tentar Novamente e Sair;
- proporções verde/vermelha dos controles nas posições esperadas;
- vetos para título, ação ou layout incompatível com o resultado proposto.

O Farm de SP mantém uma janela móvel das três observações mais recentes. Sucesso ou falha exige pelo menos duas confirmações e nenhuma confirmação do estado oposto; `Unknown` não contabiliza SP. Em transições que autorizam novo input mantido, como reconhecer o HUD Tempo Restante/Atual antes de acelerar, o consenso 2/3 também exige que a observação mais recente seja positiva. Essa regra de **latest-positive** impede que dois frames antigos autorizem uma ação depois que a tela já mudou. Timeout ou conflito salva somente um diagnóstico local e interrompe o fluxo.

Antes dos farms correspondentes, `RequiredCarSelector` confirma o Subaru Impreza 22B-STI do Farm de SP ou a Nissan S-Cargo S1 800 do Farm de CR. A seleção ocorre em camadas:

1. confirma o menu da rua e lê o cabeçalho atual em três capturas;
2. reconhece a grade de fabricantes por título, distribuição das células e contorno lime do foco;
3. associa tokens OCR às células fixas da grade de carros e exige um único foco clássico coerente com o cartão;
4. para a S-Cargo, lê o PI no painel de detalhes e exige S1 800 em duas de três leituras sem classe conflitante;
5. antes de entrar no carro, readquire fabricante, cartão, célula, foco e, quando aplicável, classe;
6. após a entrega, aguarda passivamente a rua/menu e relê o cabeçalho do carro antes de permitir o farm.

Movimentos, sondas e correções são limitados. Perda da grade, fabricante diferente, múltiplos candidatos, foco ambíguo, PI ausente/conflitante ou falha na releitura final produz `CalibrationRequiredException`; o BOT não presume que a troca funcionou nem inicia o farm com um carro não confirmado.

## Contrato de segurança do Farm de CR

O erro caro é um falso positivo: manter o freio de mão por 25 segundos quando o carro não está encaixado. Por isso:

1. a aproximação usa uma rampa analógica curta via ViGEm;
2. uma captura é solicitada imediatamente após soltar o acelerador;
3. ao surgir um candidato, o freio de mão é acionado de forma protetiva antes de logs ou cópias de dataset;
4. três frames novos são capturados sob o freio;
5. todos precisam atingir o maior valor entre 90% e `ValidThreshold`;
6. qualquer timeout, `Invalid`, `Unknown`, erro ou cancelamento libera o freio;
7. somente um consenso `Valid` mantém o mesmo acionamento até completar 25 segundos;
8. o BOT reinicia a posição e termina a corrida;
9. duas leituras concordantes do saldo confirmam o resultado econômico.

O retorno ao menu da rua, sozinho, não é ground truth. Uma tentativa só é `Valid` quando o delta de CR atinge `MinimumSuccessfulCreditGain`; retorno à rua com delta insuficiente é `Invalid`. Permanecer no `EventMenu` depois da execução é uma falha operacional confirmada e também produz `Invalid`. Leituras ou contextos inconclusivos permanecem `Pending`.

## Caminhos e ciclo das amostras

O diretório base depende do modo de distribuição:

- instalado: `%LOCALAPPDATA%\FH6 Open Assist\ExemplosPosition`;
- portátil: `ExemplosPosition` ao lado de `FH6OpenAssist.exe`.

Estrutura produzida pelo coletor:

```text
ExemplosPosition/
├── Pending/
│   └── <attempt-id>/
│       ├── <attempt-id>__frame01.jpg
│       ├── <attempt-id>__frame02.jpg
│       ├── <attempt-id>__frame03.jpg
│       └── attempt.json
└── Dataset/
    ├── Invalid/
    └── Valid/
```

`Pending` significa ausência de ground truth e nunca deve ser usado diretamente no treino. O coletor só move uma tentativa para `Dataset` depois de um resultado confirmado: delta de CR quando volta à rua ou `EventMenu` persistente como falha operacional. Uma revisão humana pode criar uma classificação separada, mas deve registrar a proveniência e não fingir que houve confirmação por CR.

O script de treino usa o dataset **dentro do checkout** por padrão:

```text
<repo>/ExemplosPosition/Dataset/{Invalid,Valid}
```

Ele não importa automaticamente amostras de `%LOCALAPPDATA%`. A curadoria entre esses locais é manual e os arquivos continuam ignorados pelo Git.

## Privacidade

Frames e diagnósticos podem mostrar saldo, gamertag, notificações ou outros elementos da tela. Portanto:

- nunca faça `git add` de `ExemplosPosition/`, `diagnostics/`, logs ou evidências;
- não anexe frames brutos a PRs ou issues públicas;
- ao compartilhar um diagnóstico indispensável, recorte ou anonimize os dados pessoais;
- publique somente código, o ONNX exportado e seus metadados reproduzíveis;
- mantenha nomes de grupos sem informações pessoais.

Recortar ou ampliar uma ROI não anonimiza seu conteúdo. Texto OCR incluído em logs e PNGs de diagnóstico continua sendo dado local potencialmente sensível; use-o apenas para depuração, aplique a mesma retenção restrita das capturas e nunca o copie para documentação pública sem revisão.

## Preparar o ambiente Python

No PowerShell:

```powershell
py -m venv .venv
.\.venv\Scripts\python.exe -m pip install --upgrade pip
.\.venv\Scripts\python.exe -m pip install -r .\tools\cr-position-model\requirements.txt
```

As versões de PyTorch, NumPy, Pillow, ONNX, ONNX Script e ONNX Runtime estão fixadas no arquivo de requirements.

## Curar o dataset

1. Mantenha apenas `Invalid` e `Valid` como classes de treino.
2. Use o padrão `<attempt-id>__<frame-id>` para todas as capturas da mesma tentativa.
3. Nunca coloque frames de uma tentativa em classes diferentes.
4. Preserve casos difíceis e visualmente próximos nas classes corretas.
5. Não transforme uma previsão rejeitada em label apenas porque o usuário acredita que ela pontuaria; registre como revisão humana ou produza uma confirmação econômica/operacional.
6. Busque origens independentes: sessões, iluminação, clima, câmera e estados de movimento diferentes.
7. Evite múltiplos frames quase idênticos como substitutos de diversidade real.

## Treinar e exportar

Com o dataset padrão:

```powershell
.\.venv\Scripts\python.exe .\tools\cr-position-model\train.py
```

Parâmetros úteis:

```powershell
.\.venv\Scripts\python.exe .\tools\cr-position-model\train.py `
  --dataset .\ExemplosPosition\Dataset `
  --output .\Assets\Vision\cr-position.onnx `
  --metadata .\Assets\Vision\cr-position-model.json `
  --seed 20260820 `
  --epochs 60 `
  --repeats 32 `
  --batch-size 16 `
  --threads 2
```

O treino final usa todas as amostras curadas, enquanto uma segunda execução interna mede um holdout agrupado por tentativa/origem. O script também:

- exporta ONNX opset 18;
- executa `onnx.checker`;
- compara as saídas PyTorch e ONNX Runtime;
- mede latência de inferência ONNX em CPU;
- grava parâmetros, pré-processamento, split, métricas e limitações no JSON.

Sempre versione `cr-position.onnx` e `cr-position-model.json` juntos. Não edite o JSON manualmente.

## Interpretar as métricas

- **Holdout agrupado:** é a principal métrica disponível, mas só representa generalização quando há várias origens independentes por classe.
- **Ressubstituição:** confirma que o modelo ajustou/exportou as amostras usadas; não mede generalização.
- **Falso positivo:** posição aceita que não gera o delta mínimo de CR. É o erro prioritário.
- **Falso negativo:** posição operacionalmente válida rejeitada pelo modelo. Exige confirmação econômica ou revisão humana explícita.
- **Unknown:** decisão conservadora entre os limiares; não é acerto nem ground truth.

O modelo atual é descrito nos próprios metadados como bootstrap conservador. Não altere o limiar de `0,90` para compensar um dataset pequeno. Primeiro aumente a diversidade e confirme o comportamento ao vivo.

## Contrato de pré-processamento e runtime

O classificador espera:

- entrada RGB `float32`, NCHW, `[1, 3, 96, 160]`;
- crop normalizado configurado em `Assets/automation.json`;
- resize bilinear;
- escala `uint8 / 255` e normalização com média/desvio `0,5`;
- saída de dois logits: `Invalid`, `Valid`;
- softmax aplicado fora do modelo.

O runtime cria uma sessão persistente e sequencial, usa CPU com uma thread, desativa arena/memory pattern e infere somente nos checkpoints do Farm de CR. Alterações no crop, dimensões, ordem de cores ou normalização exigem regenerar modelo, metadados e configuração de forma coerente.

## Auditar visão clássica

Instale as dependências opcionais:

```powershell
.\.venv\Scripts\python.exe -m pip install -r .\tools\vision\requirements.txt
```

Depois execute:

```powershell
.\.venv\Scripts\python.exe .\tools\vision\analyze_classical_states.py
```

Por padrão, o utilitário procura `ExemplosPosition/menu_rua.png`, `menu_evento.png` e diagnósticos locais. Ele reproduz apenas as regras implementadas no script e ainda não cobre todas as telas do detector C#, como `EventPreRaceMenu`. Mudanças válidas precisam ser portadas explicitamente para `ClassicalGameStateDetector.cs`.

As duas imagens de menu são pré-requisitos locais e não acompanham o repositório. O script lança erro se qualquer uma estiver ausente. Crie-as a partir de capturas próprias, revise a privacidade e mantenha-as dentro de `ExemplosPosition/`, que é ignorado pelo Git.

## Checklist para mudanças de visão

- A responsabilidade continua correta: clássico para estável, ONNX para variável?
- OCR e clássico analisam o mesmo frame?
- ROIs ampliadas respeitam o limite de dimensão e continuam vinculadas ao checkpoint que autorizou a decisão?
- O consenso temporal exige ausência de conflito e, nas transições de ação, a observação mais recente positiva?
- Fabricante, cartão, foco, classe e confirmação pós-entrega falham fechados quando qualquer evidência fica inconclusiva?
- Conflitos e estados desconhecidos falham fechados?
- Captura, OCR e inferência ocorrem apenas nos checkpoints necessários?
- Cancelamento libera inputs e encerra a captura pendente?
- O modelo e o JSON foram regenerados juntos?
- O holdout mantém grupos inteiros e reporta cobertura por classe?
- Falsos positivos e falsos negativos foram auditados separadamente?
- Nenhum frame, log, diagnóstico ou dado pessoal entrou no diff?
- O resultado real foi confirmado pelo delta de CR ou por `EventMenu` persistente como `Invalid` operacional?

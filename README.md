# FH6 Open Assist

Assistente de automação de código aberto para **Forza Horizon 6**, feito em WinUI 3 para reduzir tarefas repetitivas no Windows com verificações visuais e paradas seguras.

> [!IMPORTANT]
> Este é um projeto independente, não oficial e sem vínculo com Microsoft, Xbox ou Playground Games. O uso de automações pode contrariar as regras do jogo. Use por sua conta e risco.

## Baixar e executar

1. Abra a página de [Releases](https://github.com/FH6-Open-Assist/FH6-Open-Assist/releases/latest).
2. Em uma versão que ofereça os artefatos atuais, escolha:
   - `FH6-Open-Assist-Setup.exe`: instala atalhos e desinstalador;
   - `FH6-Open-Assist-Portable.zip`: extraia tudo e execute `FH6OpenAssist.exe`.
3. Instale o [ViGEmBus oficial](https://github.com/nefarius/ViGEmBus/releases/latest) e reinicie o computador se pretende usar o segundo plano **ou o Farm de CR**. A verificação do instalador é apenas indicativa; o aplicativo valida uma conexão real.
4. Abra o Forza Horizon 6, prepare a tela indicada para o BOT e inicie o FH6 Open Assist.

O pacote é autossuficiente e não exige a instalação separada do .NET. No portátil, não remova `portable.marker`: ele mantém preferências, logs, diagnósticos e amostras junto do aplicativo.

> [!NOTE]
> Releases antigas podem exibir apenas `FH6-Open-Assist-win-x64.zip`. Esse é um pacote legado e não representa o pipeline atual de instalador + portátil.

> [!WARNING]
> A distribuição ainda precisa ser validada em uma instalação limpa do Windows e quanto à presença ou necessidade do Microsoft Visual C++ Redistributable. Até essa validação, não afirmamos ausência de outros pré-requisitos de sistema.

## Uso rápido

1. Selecione um dos quatro BOTs.
2. Escolha **Primeiro plano** ou **Segundo plano experimental**.
3. Abra **Instruções** e prepare o jogo.
4. Clique em **Ativar BOT**.
5. Use os atalhos globais:

| Atalho | Ação |
|---|---|
| `F8` | Inicia o BOT armado ou pausa a execução atual |
| `F9` | Encerra, solta todas as entradas e desarma o BOT |

Ao pausar, o fluxo atual é cancelado com segurança. Um novo `F8` reinicia o workflow a partir da tela em que o jogo estiver.

### Modos de entrada

- **Primeiro plano:** recomendado. O aplicativo valida e focaliza a janela do Forza antes de usar teclado/mouse. O Farm de CR também usa o controle virtual para o acelerador analógico.
- **Segundo plano experimental:** usa captura WGC e controle Xbox 360 virtual via ViGEm sem trazer o jogo para frente. O Forza pode ficar coberto, mas não minimizado.

## Instruções dos BOTs

### 1 — Skill Points

- Selecione o **Subaru Impreza 22B-STI Version**, de preferência com a árvore de habilidades desbloqueada.
- Ative todas as assistências e vá para a rua.
- O BOT abre o desafio EventLab configurado e repete a corrida até `F8`.
- Não inicie dentro da garagem.

### 2 — Farm de CR

- Selecione o **Nissan S-Cargo S1 800**, sem tunagem.
- Desative todas as assistências e coloque a dificuldade em **Imbatível**.
- Instale o **ViGEmBus**, inclusive para usar este BOT em primeiro plano.
- Vá para a rua; não inicie dentro da garagem.

O fluxo lê o saldo antes da tentativa, abre o evento, aproxima o carro lentamente e aciona um freio de mão protetivo assim que surge um candidato. O modelo ONNX só autoriza a continuidade após três frames sob o freio atingirem o limiar conservador. Depois de 25 segundos, o BOT reinicia a posição e conclui a corrida. Sucesso é confirmado pelo aumento real de CR; permanência inequívoca no menu do evento confirma falha operacional. OCR ou contextos inconclusivos causam recuperação limitada ou parada segura.

### 3 — WheelSpin Mad Mike

- A conta precisa ser **VIP**.
- Tenha ao menos **100.000 CR** e **30 SP** por ciclo.
- Comece na garagem, no menu **Campanha**.

Cada ciclo lê os recursos, compra um Mad Mike, desbloqueia a Maestria, troca para outro carro e remove um Mad Mike compatível encontrado pelo filtro visual de modelo/preço. O fluxo não rastreia a identidade individual do carro recém-comprado; se já houver cópias iguais, uma delas pode ser removida. Use somente se aceita essa alteração destrutiva na garagem.

### 4 — Gastar Wheelspins

- Comece na rua, no menu de pausa ou em uma tela de Wheelspin.
- O BOT prioriza Super Wheelspins e só gira após confirmações OCR do estado.
- Saldo zero encerra sem comprar giros com créditos.
- Carros duplicados são mantidos; vender ou presentear não é escolhido automaticamente.

## Requisitos

- Windows 10 build 19041 ou superior, ou Windows 11, em x64.
- Interface do jogo em português do Brasil e pacote de OCR correspondente disponível no Windows.
- Resolução 16:9 recomendada e jogo aberto, renderizando e não minimizado.
- ViGEmBus funcional para segundo plano e para o Farm de CR em qualquer modo.
- Tempos de carregamento, resolução, FPS, atualizações do jogo e alterações de interface podem exigir nova calibração.

## Como funciona

| Camada | Responsabilidade |
|---|---|
| WinUI 3 | Interface, preferências, estados do BOT, log limitado e encerramento seguro |
| `AutomationCoordinator` | Seleção, armação, cancelamento, erros e liberação final de entradas/captura |
| Entrada Windows/ViGEm | Teclado e mouse validados no primeiro plano; controle virtual no segundo plano e aceleração analógica |
| Windows Graphics Capture | Captura a janela por HWND apenas nos checkpoints e libera a sessão quando ociosa |
| OCR do Windows | Lê menus, recursos, confirmações e contexto variável em português |
| Visão clássica | Confirma layouts estáveis por geometria e cores, como menus e diálogos |
| ONNX | Resolve o problema variável de alinhamento do carro entre as placas do Farm de CR |

O runtime não usa OpenCV diretamente. O detector clássico do aplicativo é C# determinístico; OpenCV fica no utilitário offline de auditoria. OCR e visão clássica analisam o mesmo checkpoint e qualquer conflito vira `Unknown`. O ONNX roda em CPU, uma thread, com sessão persistente e somente nos pontos de decisão — não existe inferência contínua durante o jogo.

Os PNGs legados em `Assets/Vision` são referências de calibração e não participam de template matching no runtime atual.

Mais detalhes: [Visão, coleta e treinamento](docs/VISION_AND_TRAINING.md).

## Dados locais e privacidade

| Dado | Instalado | Portátil |
|---|---|---|
| Preferências | `%LOCALAPPDATA%\FH6 Open Assist\user-preferences.json` | ao lado do executável |
| Logs diários | `%LOCALAPPDATA%\FH6 Open Assist\logs` | `logs` ao lado do executável |
| Diagnósticos | `%LOCALAPPDATA%\FH6 Open Assist\diagnostics` | `diagnostics` ao lado do executável |
| Amostras do Farm de CR | `%LOCALAPPDATA%\FH6 Open Assist\ExemplosPosition` | `ExemplosPosition` ao lado do executável |

Logs, diagnósticos e amostras podem conter informações visíveis da conta ou da tela do jogo. Revise e remova dados pessoais antes de compartilhar qualquer trecho. `ExemplosPosition/`, `diagnostics/` e logs são ignorados pelo Git e não fazem parte do repositório.

## Executar pelo código-fonte

Instale o [SDK do .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) e use PowerShell no Windows:

```powershell
git clone https://github.com/FH6-Open-Assist/FH6-Open-Assist.git
cd FH6-Open-Assist
dotnet restore .\FH6OpenAssist.csproj -r win-x64
dotnet build .\FH6OpenAssist.csproj -c Release --no-restore
dotnet run -c Release --project .\FH6OpenAssist.csproj --no-restore
```

Não existe atualmente uma suíte automatizada. O CI valida restauração, publicação autossuficiente e os dois pacotes de distribuição.

## Treinar o modelo ONNX

O treino é offline e usa apenas um dataset local curado. A coleta do aplicativo e o dataset do checkout não são sincronizados automaticamente.

```powershell
py -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\tools\cr-position-model\requirements.txt
.\.venv\Scripts\python.exe .\tools\cr-position-model\train.py
```

O comando espera `ExemplosPosition\Dataset\Invalid` e `Valid`, mantém todos os frames de uma tentativa no mesmo grupo e gera em conjunto:

- `Assets\Vision\cr-position.onnx`;
- `Assets\Vision\cr-position-model.json`.

O JSON contém arquitetura, pré-processamento, split agrupado, métricas, equivalência ONNX e limitações. O modelo atual é conservador e seus próprios metadados não alegam generalização ampla. Nunca publique os frames usados no treino.

## Auditar parte da visão clássica com OpenCV

O utilitário opcional reproduz offline um subconjunto das regras de cor/layout do runtime. Ele cobre as referências estáveis disponíveis, mas ainda não implementa todas as telas do detector C#, como `EventPreRaceMenu`.

Antes de executá-lo, forneça localmente `ExemplosPosition\menu_rua.png` e `ExemplosPosition\menu_evento.png`. Essas referências são privadas, ignoradas pelo Git e não acompanham um clone novo; sem ambas, o comando encerra com erro.

```powershell
.\.venv\Scripts\python.exe -m pip install -r .\tools\vision\requirements.txt
.\.venv\Scripts\python.exe .\tools\vision\analyze_classical_states.py
```

Ele lê referências locais e diagnósticos, mas não é carregado pelo executável.

## Gerar uma distribuição

Com o Inno Setup 6 instalado:

```powershell
.\scripts\build-release.ps1 -Version 0.0.0-local
```

Um único staging de `dotnet publish` gera em `artifacts\release`:

- `FH6-Open-Assist-Portable.zip`, com `portable.marker`;
- `FH6-Open-Assist-Setup.exe`, sem o marcador.

Tags SemVer como `v1.2.3` acionam o workflow de GitHub Release.

## Diagnóstico e suporte

O painel **Log da execução** registra decisões e estados sem crescer indefinidamente. Em falhas de reconhecimento, o aplicativo pode salvar um diagnóstico no diretório de dados descrito acima. Ao abrir uma issue, informe BOT, modo, tela inicial, resolução/FPS, número da tentativa e apenas o trecho necessário do log. Não anexe datasets ou capturas sem revisar a privacidade.

Pull requests são bem-vindos. Leia o [guia de contribuição](CONTRIBUTING.md), use [issues](https://github.com/FH6-Open-Assist/FH6-Open-Assist/issues) para problemas reproduzíveis e [Discussões](https://github.com/FH6-Open-Assist/FH6-Open-Assist/discussions) para dúvidas e ideias.

## Apoie o projeto

Se o FH6 Open Assist ajudou você, o apoio é voluntário e contribui para a manutenção.

**PIX:** `48bf874c-3e3d-48d1-89eb-4cd11b679167`

<p align="center">
  <img src="docs/pix-qrcode.png" alt="QR Code PIX para apoiar o FH6 Open Assist" width="430">
</p>

## Licença

Distribuído sob a [GNU General Public License v3.0](LICENSE).

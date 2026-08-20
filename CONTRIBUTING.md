# Como contribuir

Obrigado por querer melhorar o FH6 Open Assist. O projeto automatiza entradas reais no jogo; mudanças devem priorizar estado conhecido, cancelamento e recuperação segura.

## Antes de começar

- Use Windows 10 build 19041+ ou Windows 11 x64.
- Instale o SDK do .NET 10.
- Leia as instruções do BOT e [a arquitetura de visão](docs/VISION_AND_TRAINING.md).
- Procure uma issue ou PR equivalente. Para alterações grandes de comportamento, abra primeiro uma solicitação de melhoria.
- Nunca use dados, capturas ou logs de outra pessoa sem autorização.

## Preparar o projeto

1. Faça um fork e clone o repositório.
2. Crie uma branch curta, como `fix/cr-handbrake-timing` ou `feat/wheelspin-recovery`.
3. Restaure e compile:

```powershell
dotnet restore .\FH6OpenAssist.csproj -r win-x64
dotnet build .\FH6OpenAssist.csproj -c Release --no-restore
```

O projeto é WinUI 3 não empacotado, self-contained, .NET 10 e x64. `MainWindow.xaml.cs` é o composition root; mantenha `AutomationContext` e o descarte de dependências coerentes.

## Onde alterar

- `Core/`: contratos, estados, configurações, caminhos, recursos e coordenação.
- `Workflows/`: máquinas de estado dos quatro BOTs e navegação do jogo.
- `Windows/`: HWND, foco, hotkeys, teclado/mouse e controle virtual ViGEm.
- `Vision/`: WGC, OCR, visão clássica, contexto, ONNX e coleta.
- `Assets/automation.json`: calibração/runtime; alterações devem permanecer compatíveis com os defaults de `AutomationSettings`.
- `tools/`: treino e auditoria offline; essas dependências não devem vazar para o executável.
- `scripts/` e `installer/`: geração do ZIP portátil e do instalador.

## Regras de automação

- Propague `CancellationToken` em operações assíncronas.
- Libere teclas, botões, gatilhos e captura em `finally` ou em uma barreira equivalente.
- No primeiro plano, valide processo, HWND e foco antes de enviar entrada.
- No segundo plano, não foregrounde o jogo e falhe se ViGEm não estiver funcional.
- Use timeouts e tentativas limitadas. Um estado desconhecido não autoriza input destrutivo.
- Mudanças temporais devem informar a tela inicial, FPS, modo, duração medida e motivo da calibração.
- Farm de CR depende de ViGEm também no primeiro plano.

## Visão clássica, OCR e ONNX

- Use OCR para texto e números variáveis.
- Use visão clássica C# para layouts simples e estáveis. OpenCV é apenas a ferramenta de auditoria offline.
- Reserve ONNX para variação visual que realmente precise de treino. Hoje ele decide o alinhamento entre as placas do Farm de CR.
- Se OCR e clássico discordarem, preserve o resultado `Unknown`.
- Não troque o consenso temporal do ONNX por aceitação de um único frame.
- Não reduza o piso de 90% sem dataset independente e validação econômica.

### Alterar o modelo de posição

Prepare o ambiente:

```powershell
py -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\tools\cr-position-model\requirements.txt
```

O dataset local deve ter:

```text
ExemplosPosition/Dataset/Invalid
ExemplosPosition/Dataset/Valid
```

Todos os frames de uma tentativa precisam compartilhar o prefixo `<attempt-id>__` e permanecer no mesmo grupo. `Pending`, `Unknown` e a previsão do modelo não são ground truth. Use resultado econômico confirmado, falha operacional inequívoca em `EventMenu` ou revisão humana explicitamente registrada.

Treine com:

```powershell
.\.venv\Scripts\python.exe .\tools\cr-position-model\train.py
```

Inclua juntos no PR:

- `Assets/Vision/cr-position.onnx`;
- `Assets/Vision/cr-position-model.json`;
- mudanças de código/configuração necessárias.

Descreva cobertura do holdout agrupado, falsos positivos, falsos negativos, `Unknown`, equivalência ONNX e latência. Ressubstituição não comprova generalização.

### Alterar a visão clássica

Prepare localmente `ExemplosPosition\menu_rua.png` e `ExemplosPosition\menu_evento.png`. Esses arquivos são privados e ignorados; o utilitário falha deliberadamente quando as referências não existem.

Instale e execute a ferramenta offline:

```powershell
.\.venv\Scripts\python.exe -m pip install -r .\tools\vision\requirements.txt
.\.venv\Scripts\python.exe .\tools\vision\analyze_classical_states.py
```

Use-a para medir referências locais, mas lembre que sua cobertura é parcial e ainda não inclui todas as telas do detector C#, como `EventPreRaceMenu`. Replique a regra final no detector C# e valide conflitos com OCR.

## Dados proibidos no Git

Não envie:

- `ExemplosPosition/`, `diagnostics/`, logs ou evidências de tentativas;
- `bin/`, `obj/`, `publish/`, `artifacts/` ou ambientes Python;
- credenciais, tokens, gamertags, saldos associados a pessoas ou outras informações pessoais;
- prints brutos em commits, PRs ou issues.

O ONNX exportado, os metadados reproduzíveis e o código de treino podem ser versionados. Revise nomes/caminhos dos metadados antes de publicar.

## Validar a alteração

Não há suíte automatizada. Faça a menor validação suficiente e informe o que não foi testado.

| Alteração | Validação mínima |
|---|---|
| Documentação/YAML | Renderização, links/comandos e `git diff --check` |
| C#/XAML/assets runtime | Restore + build Release |
| Input/cancelamento | F8/F9, perda de foco/janela e soltura de todas as entradas |
| Segundo plano | Forza coberto, não minimizado e sem ganhar foco |
| Workflow do jogo | Tela inicial, modo, resolução/FPS, logs e resultado real |
| Farm de CR/ONNX | Consenso sob freio, tempo de soltura→freio e delta de CR; `EventMenu` persistente para `Invalid` operacional |
| Empacotamento | ZIP + Setup a partir do mesmo staging |

## Enviar o pull request

Abra o PR contra `main` e preencha o template. O projeto exige build, aprovação de CODEOWNER, branch atualizada e conversas resolvidas. O merge é por squash; o título deve resumir a mudança final.

Não use screenshots como substituto de evidência textual. Se uma mudança visual realmente exigir imagem, anonimize-a antes de anexar.

## Distribuição e release

Com Inno Setup 6 instalado:

```powershell
.\scripts\build-release.ps1 -Version 0.0.0-local
```

O comando deve gerar em `artifacts\release`:

- `FH6-Open-Assist-Portable.zip`, com `portable.marker`;
- `FH6-Open-Assist-Setup.exe`, sem o marcador.

Uma tag SemVer `vX.Y.Z` aciona o release. Antes de declarar ausência de pré-requisitos adicionais, valide em Windows limpo e confirme a necessidade do Microsoft Visual C++ Redistributable.

## Segurança

Não abra issue pública para vulnerabilidades ou credenciais expostas. Use **Security → Report a vulnerability** no repositório.

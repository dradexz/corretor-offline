# Corretor Offline

Corretor de texto para Windows que usa o atalho **Ctrl + Alt + Espaço**. Corrige português brasileiro e inglês localmente, usando o texto do campo como contexto. Se houver uma seleção, só aquele trecho é alterado.

## Como usar

1. Abra o aplicativo. Ele fica na bandeja, perto do relógio.
2. Escreva em um campo editável. Se quiser corrigir apenas um trecho, selecione-o.
3. Pressione **Ctrl + Alt + Espaço** e solte as teclas.
4. Confira a correção. O envio é feito pelo próprio usuário.

No menu da bandeja você pode trocar o atalho, escolher o idioma, ativar a prévia ou iniciar o aplicativo com o Windows.

## Compilar no Windows

Você precisa do SDK do .NET 8 e do runtime de desktop do .NET 8. Em um PowerShell aberto na pasta do código, execute:

```powershell
.\Baixar-Dicionarios.ps1
dotnet build -c Release
dotnet run -c Release --no-build
```

A preparação baixa o Hunspell via NuGet e os dicionários do LibreOffice via HTTPS. O script fixa a revisão dos dicionários e confere os hashes dos arquivos. Depois da preparação, o aplicativo corrige os textos offline.

## Como funciona

- `Program.cs` cuida da bandeja, do atalho e do processo de análise sob demanda.
- `Engine.cs` detecta o idioma, usa dicionários Hunspell e aplica regras de ortografia e gramática.
- `WindowsEdit.cs` verifica o foco e a seleção por meio de APIs do Windows antes de substituir o texto.

Exemplo em português: “Eu vou postar videos todos os dias.” → “Eu vou postar vídeos todos os dias.” Em uma frase em inglês, a grafia usada é “videos”.

## Compatibilidade

Testado no Bloco de Notas e em campos locais do Brave e do Edge. Campos protegidos e controles cuja edição não pode ser verificada são ignorados. No Chromium, uma correção longa pode precisar de vários Ctrl+Z para ser desfeita.

Os dicionários e a biblioteca mantêm suas licenças de origem. [Dicionários do LibreOffice](https://github.com/LibreOffice/dictionaries/tree/4f1df64b2e8e14751aa7c2205d170ca016071315) · [WeCantSpell.Hunspell](https://github.com/aarondandy/WeCantSpell.Hunspell)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using WeCantSpell.Hunspell;

internal record Request(string Text, string Language, bool Selection, string? Context = null);
internal record Correction(string Text, bool Review);

internal static class Engine
{
    internal const int MaxText = 12000;
    private static readonly Regex Word = Rx(@"[\p{L}\p{M}]+(?:['’\-][\p{L}\p{M}]+)*");
    private static readonly Regex Protected = Rx(
        @"```[\s\S]*?(?:```|\z)|`[^`\r\n]+`|(?:https?://|www\.)[^\s<>]+|[\w.+-]+@[\w.-]+\.[a-zA-Z]{2,}|(?<!\w)[@#][\p{L}\p{M}\d_.-]+|\b[A-Za-z]:\\[^\r\n<>]+|(?<!\w)(?:\w+[._][\w.]+|(?=\w*\d)\w*\p{L}\w*)(?!\w)",
        RegexOptions.IgnoreCase);
    private static readonly HashSet<string> PtMarkers = Set(
        "ola olá oi voce você voces vocês nao não eu nós nos minha meu minhas muito tudo bem com como queria saber amanha amanhã onde quando porque então tambem também para gente pode poderia fazer vou vamos que se está estávamos estou tem tenho as os um uma isso seu sua obrigado obrigada esses essas acho mensagem pronto pronta postar gravar assistir novos novas hoje todos dias aqui ali inscritos legenda");
    private static readonly HashSet<string> EnMarkers = Set(
        "hello hey hi how are you your i i'm ive i've am was were is it we they he she this that these those the and with will would could should have has can want thanks tomorrow please where which what why just very good watch upload uploaded new latest some my every day here from short funny amazing");
    private static readonly HashSet<string> VideoForms = Set("video videos vídeo vídeos");
    private static readonly HashSet<string> Slang = Set(
        "aff blz cê cês ce vcs vc vdd tb tbm pq q qq mano man kkk kkkkk rs rsrs huehue bora tá tô to tava tavam tamo pra pras pro pros vlw flw tmj mano véi vei vey mano ué uai poxa eita opa bah né pow salve obg mds n s mto msm msg hj mano manozo lol lmao lmfao rofl brb omg tbh idk btw gonna gotta wanna kinda sorta yep nope y'all yall bro dude ok okay nah hey yo pls thx ty asap");
    private static readonly Dictionary<string, string> Names = Map(
        "chatgpt=ChatGPT whatsapp=WhatsApp discord=Discord telegram=Telegram windows=Windows obs=OBS amd=AMD ryzen=Ryzen radeon=Radeon github=GitHub youtube=YouTube iphone=iPhone openai=OpenAI google=Google");
    private static readonly Dictionary<string, string> PtTypos = Map(
        "ola=olá voce=você voces=vocês amanha=amanhã nao=não tambem=também ja=já so=só ate=até entao=então informacao=informação informacoes=informações duvida=dúvida duvidas=dúvidas alguem=alguém ninguem=ninguém horario=horário horarios=horários noticia=notícia noticias=notícias endereco=endereço enderecos=endereços correcao=correção portugues=português ingles=inglês gramatica=gramática gramaticalmente=gramaticalmente otimo=ótimo otima=ótima possivel=possível necessario=necessário necessaria=necessária facil=fácil dificil=difícil util=útil rapida=rápida rapido=rápido rapido=rápido parabens=parabéns reuniao=reunião reunioes=reuniões atencao=atenção sabado=sábado ultima=última ultimo=último saude=saúde menssagem=mensagem excessao=exceção concerteza=certeza derrepente=repente presciso=preciso concerteza=certeza anxioso=ansioso proplema=problema progeto=projeto encomodar=incomodar esperiencia=experiência");
    private static readonly Dictionary<string, string> EnTypos = Map(
        "teh=the adn=and thier=their thsi=this recieve=receive recieved=received recieving=receiving tommorow=tomorrow tomorow=tomorrow definately=definitely definetly=definitely seperate=separate seperately=separately becuase=because becase=because freind=friend freinds=friends langauge=language wierd=weird adress=address addres=address mesage=message messege=message alraedy=already occured=occurred writting=writing tought=thought");

    private static readonly (string, string)[] PtGrammar =
    {
        (@"\b(se[ \t]+(?:(?:agora|amanhã|hoje)[ \t]+)?)agente(?=\s+(?:pode|podia|vai|quer|queria|precisa|precisava|consegue|conseguiu|combinou|se\s+(?:vê|encontra))\b)", "$1a gente"),
        (@"\b(pode|podem|podemos|podia|poderia|preciso|precisa|quero|queria|vou|vamos)\s+((?:me|te|se|nos|lhe)[ \t]+)?(conversa|fala|manda|espera|explica|ajuda|trabalha|chega|marca|compra|confirma|escreve|responde|decide|revisa|estuda|viaja|termina|começa|verifica|compara|encontra|olha|escuta|entrega)\b", "$1 $2$3r"),
        (@"\bconcerteza\b", "com certeza"),
        (@"\bderrepente\b", "de repente"),
        (@"\bnada\s+(?:haver|a\s+haver)\s+com\b", "nada a ver com"),
        (@"\bassim\s+que\s+eu\s+chegar\s+eu\b", "assim que eu chegar, eu"),
        (@"\b(?:nós|nos)\s+vai\b", "nós vamos"),
        (@"\bnós\s+foi\b", "nós fomos"),
        (@"\bnós\s+tem\b", "nós temos"),
        (@"\bnós\s+quer\b", "nós queremos"),
        (@"\bnós\s+precisa\b", "nós precisamos"),
        (@"\bnós\s+está\b", "nós estamos"),
        (@"\b(eu)\s+foi\b", "$1 fui"),
        (@"\b(eu)\s+gosta\s+de\b", "$1 gosto de"),
        (@"\b(eu)\s+tem\s+(uma?|que|medo|dúvidas?)\b", "$1 tenho $2"),
        (@"\b(eles|elas)\s+vai\b", "$1 vão"),
        (@"\b(eles|elas)\s+foi\b", "$1 foram"),
        (@"\b(eles|elas)\s+está\b", "$1 estão"),
        (@"\b(eles|elas)\s+tem\b", "$1 têm"),
        (@"\b(eles|elas)\s+precisa\b", "$1 precisam"),
        (@"\b(eles|elas)\s+gosta\s+de\b", "$1 gostam de"),
        (@"\b(a gente)\s+vamos\b", "$1 vai"),
        (@"\b(a gente)\s+fomos\b", "$1 foi"),
        (@"\b(a gente)\s+temos\b", "$1 tem"),
        (@"\b(as minhas|as suas|as nossas)\s+mensagem\b", "$1 mensagens"),
        (@"\b(as|essas|aquelas|duas|três)\s+(coisa|pessoa|mensagem|dúvida|resposta|palavra|pergunta|notícia|reunião)\b", "$1 $2s"),
        (@"\b(reunião)s\b", "reuniões"),
        (@"\b(os|esses|aqueles|dois|três)\s+(erro|teste|arquivo|problema|amigo|pedido|documento|resultado)\b", "$1 $2s"),
        (@"\b(os arquivos|os documentos|os pedidos)\s+(está|esta)\s+pronto\b", "$1 estão prontos"),
        (@"\b(as (?:(?:minhas|suas|nossas)\s+)?(?:coisas|mensagens|respostas))\s+(está|esta)\s+pronta\b", "$1 estão prontas"),
        (@"\b(fazem)\s+(dois|três|quatro|cinco|seis|sete|oito|nove|dez|\d+)\s+(dias|semanas|meses|anos)\s+que\b", "faz $2 $3 que"),
        (@"(?<=\p{L})\s+mas(?=\s+(?:eu|nós|ela|ele|eles|elas|você|vocês|a gente)\b)", ", mas"),
    };
    private static readonly (string, string)[] EnGrammar =
    {
        (@"\bi(?=\s|[,.!?;:'’]|$)", "I"),
        (@"\bim\s+(going|fine|happy|sorry|sure|ready|working|writing|waiting|not|here|trying)\b", "I'm $1"),
        (@"\bdont\b", "don't"),
        (@"\bdoesnt\b", "doesn't"),
        (@"\bdidnt\b", "didn't"),
        (@"\bwont\b", "won't"),
        (@"\bcant(?=\s+(?:be|go|do|see|make|find|wait|believe|hear|tell|say|come|understand|remember)\b)", "can't"),
        (@"\b(he|she|it)\s+dont\b", "$1 doesn't"),
        (@"\b(he|she|it)\s+don't\b", "$1 doesn't"),
        (@"\b(he|she|it)\s+are\b", "$1 is"),
        (@"\b(he|she|it)\s+have\b", "$1 has"),
        (@"\b(he|she|it)\s+do\s+not\b", "$1 does not"),
        (@"\b(he|she|it)\s+go\s+to\b", "$1 goes to"),
        (@"\b(you|we|they)\s+is\b", "$1 are"),
        (@"\b(I)\s+is\b", "$1 am"),
        (@"\b(these|those)\s+is\b", "$1 are"),
        (@"\b(this|that)\s+are\b", "$1 is"),
        (@"\b(could|should|would)\s+of\b", "$1 have"),
        (@"\b(a)\s+(apple|orange|idea|hour|example|answer|email|issue|error|update)\b", "an $2"),
        (@"\b(an)\s+(university|user|useful|one-time|house|car|test|file)\b", "a $2"),
        (@"(?<=\p{L})\s+but(?=\s+(?:I|you|he|she|it|we|they)\b)", ", but"),
    };

    internal static Correction Correct(Request request, CancellationToken token)
    {
        var original = request.Text;
        if (original.Length > MaxText || !original.Any(char.IsLetter) ||
            original.Contains('\uE000') || original.Contains('\uE001') || LooksLikeCode(original))
            return new(original, false);

        var saved = new List<string>();
        var masked = Protected.Replace(original, m =>
        {
            saved.Add(m.Value);
            return "\uE000" + (saved.Count - 1).ToString(CultureInfo.InvariantCulture) + "\uE001";
        });
        var languageText = request.Context == null ? masked : Protected.Replace(request.Context, " ");
        var evidence = Word.Matches(languageText).Select(m => m.Value.ToLowerInvariant()).ToArray();
        int pt = evidence.Count(PtMarkers.Contains), en = evidence.Count(EnMarkers.Contains);
        // The spelling being corrected must not vote for its own language.
        pt += 2 * evidence.Count(w => !VideoForms.Contains(w) && w.Any(c => "áàâãéêíóôõúç".Contains(c)));
        bool? portuguese = request.Language switch
        {
            "pt-BR" => true,
            "en-US" => false,
            _ when pt > 0 && pt >= en => true,
            _ when en > pt => false,
            _ when evidence.Any(PtTypos.ContainsKey) && !evidence.Any(EnTypos.ContainsKey) => true,
            _ when evidence.Any(EnTypos.ContainsKey) && !evidence.Any(PtTypos.ContainsKey) => false,
            _ when evidence.Any(VideoForms.Contains) => true,
            _ => null
        };
        if (portuguese == null) return new(original, false);
        bool ptMode = portuguese.Value;
        var typos = ptMode ? PtTypos : EnTypos;
        var otherMarkers = ptMode ? EnMarkers : PtMarkers;
        var code = ptMode ? "pt_BR" : "en_US";
        var dict = WordList.CreateFromFiles(Path.Combine(AppContext.BaseDirectory, "dicionarios", code + ".dic"));
        token.ThrowIfCancellationRequested();

        // The rules below handle compounds before checking their individual tokens.
        if (ptMode)
        {
            masked = ReplaceRule(masked, @"\bconcerteza\b", "com certeza");
            masked = ReplaceRule(masked, @"\bderrepente\b", "de repente");
        }
        var suggestions = new Dictionary<string, string>();
        bool review = false;
        bool mixed = request.Language == "auto" && pt >= 3 && en >= 3 && Math.Max(pt, en) < 2 * Math.Min(pt, en);
        string CorrectWord(Match m)
        {
            token.ThrowIfCancellationRequested();
            var raw = m.Value;
            var lower = raw.ToLowerInvariant();
            if (Names.TryGetValue(lower, out var name)) return name;
            if (VideoForms.Contains(lower))
                return ptMode ? raw.Replace('i', 'í').Replace('I', 'Í') : raw.Replace('í', 'i').Replace('Í', 'I');
            if (Slang.Contains(lower) || otherMarkers.Contains(lower) || lower.Contains('-') || lower.Contains('\'')) return raw;
            if (typos.TryGetValue(lower, out var known))
            {
                if (lower == raw) return known;
                if (char.IsUpper(raw[0]) && raw[1..] == lower[1..] &&
                    (m.Index == 0 || masked[..m.Index].TrimEnd().EndsWith('.') ||
                     masked[..m.Index].TrimEnd().EndsWith('!') || masked[..m.Index].TrimEnd().EndsWith('?')))
                    return char.ToUpperInvariant(known[0]) + known[1..];
            }
            if (raw.Any(char.IsUpper)) return raw;
            if (mixed || lower.Length < 5 || lower.Length > 32 || dict.Check(lower, token)) return raw;
            if (!suggestions.TryGetValue(lower, out var proposed))
            {
                proposed = raw;
                var candidates = dict.Suggest(lower, token).Take(8)
                    .Where(w => w == w.ToLowerInvariant() && w.All(char.IsLetter) && !Slang.Contains(w))
                    .Where(w => Near(lower, w)).Distinct(StringComparer.Ordinal).Take(2).ToArray();
                if (candidates.Length == 1) proposed = candidates[0];
                suggestions[lower] = proposed;
            }
            if (proposed != raw) review = true;
            return proposed;
        }
        var output = new StringBuilder(masked.Length);
        int position = 0;
        for (var match = Word.Match(masked); match.Success; match = match.NextMatch())
        {
            output.Append(masked, position, match.Index - position);
            output.Append(CorrectWord(match));
            position = match.Index + match.Length;
        }
        output.Append(masked, position, masked.Length - position);
        var corrected = output.ToString();
        if (!mixed)
        {
            foreach (var (pattern, replacement) in ptMode ? PtGrammar : EnGrammar)
            {
                token.ThrowIfCancellationRequested();
                corrected = ReplaceRule(corrected, pattern, replacement);
            }
            corrected = Punctuate(corrected, ptMode, request.Selection);
        }
        corrected = Rx(@"\uE000(\d+)\uE001").Replace(corrected,
            m => saved[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]);
        return new(corrected, review);
    }

    internal static bool LooksLikeCode(string text) =>
        Rx(@"(?m)^\s*(?:[$>]\s+|#include\b|using\s+[\w.]+;|(?:from|import)\s+\w+\s*(?:$|\bimport\b)|(?:def|function|class)\s+\w+\s*[(\{:])|=>|</\w+>|\b(?:SELECT\s+.+\s+FROM|git\s+(?:status|log|diff|commit)|npm\s+(?:run|install))\b|\{[\s\S]*[;=][\s\S]*\}").IsMatch(text);

    private static string Punctuate(string text, bool pt, bool selection)
    {
        text = Rx(@"(?<=\p{L})[ \t]+([,;!?\.])(?=[ \t]|\p{L}|$)").Replace(text, "$1");
        text = Rx(@"(?<=\p{L})[,;!?](?=\p{L})").Replace(text, "$0 ");
        text = Rx(@"(?<=\S) {2,}(?=\S)").Replace(text, " ");
        if (!selection)
        {
            var greeting = pt ? @"olá|oi" : @"hello|hi|hey";
            text = ReplaceRule(text,
                @"^(\s*)(" + greeting + @")\s+(ChatGPT|\p{Lu}[\p{Ll}\p{M}]+)(?=\s+(?i:tudo bem|how are you)\b)",
                "$1$2, $3!");
            text = ReplaceRule(text, pt
                ? @"\b(tudo bem(?: com você)?|como (?:vai|você está))(?=\s+(?:eu|nós|queria|gostaria|você|pode)\b)"
                : @"\b(how are you|how is it going)(?=\s+(?:I|we|can|could|do)\b)", "$1?");
            text = ReplaceRule(text, pt
                ? @"\b(tudo bem(?: com você)?|como (?:vai|você está))(?=\s*\z)"
                : @"\b(how are you|how is it going)(?=\s*\z)", "$1?");
            if (!text.Contains('\n') && !text.Contains('\r') && Word.Matches(text).Count >= 5)
            {
                var question = pt
                    ? @"^\s*(?i:como(?!\s+(?:eu disse|combinado|sempre|se)\b)|qual|quais|quando|onde|por que|você pode|vocês podem|será que)\b"
                    : @"^\s*(?i:what|where|when|why|how(?!\s+to\b)|who|can you|could you|would you|do you|did you|are you)\b";
                var statement = pt
                    ? @"\b(?i:eu|nós|ela|ele|eles|elas|você|vocês|a gente|queria|preciso|vamos|vou|obrigado|obrigada)\b"
                    : @"\b(?i:I|we|you|he|she|they|it|please|thanks)\b";
                if (Rx(question).IsMatch(text))
                    text = Rx(@"[\p{L}\p{M}](?=\s*\z)").Replace(text, "$0?");
                else if (Rx(statement).IsMatch(text))
                    text = Rx(@"[\p{L}\p{M}](?=\s*\z)").Replace(text, "$0.");
            }
        }
        var start = selection ? @"(?<=[!?])[ \t]+\p{Ll}" : @"(?:\A[ \t]*|(?<=[!?])[ \t]+|(?<=\p{Ll}{3}\.)[ \t]+)\p{Ll}";
        return Rx(start).Replace(text, m =>
        {
            var next = m.Index + m.Length;
            // Preserve a mixed-case brand such as iPhone.
            if (next + 1 < text.Length && char.IsUpper(text[next]) && char.IsLower(text[next + 1]))
                return m.Value;
            return m.Value[..^1] + char.ToUpperInvariant(m.Value[^1]);
        });
    }

    private static string ReplaceRule(string text, string pattern, string replacement) =>
        Rx(pattern.Replace(@"\s+", @"[ \t]+"), RegexOptions.IgnoreCase).Replace(text, m =>
        {
            if (m.Value.All(c => !char.IsLower(c)) && m.Value.Length > 1) return m.Value;
            var value = m.Result(replacement);
            int first = 0;
            while (first < m.Length && !char.IsLetter(m.Value[first])) first++;
            if (first < value.Length && first < m.Length && char.IsUpper(m.Value[first]))
                return value[..first] + char.ToUpperInvariant(value[first]) + value[(first + 1)..];
            return value;
        });

    private static bool Near(string a, string b)
    {
        if (a.Length == b.Length)
        {
            var edits = Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).Take(3).ToArray();
            return edits.Length == 1 || (edits.Length == 2 && edits[1] == edits[0] + 1 &&
                a[edits[0]] == b[edits[1]] && a[edits[1]] == b[edits[0]]);
        }
        if (Math.Abs(a.Length - b.Length) != 1) return false;
        if (a.Length < b.Length) (a, b) = (b, a);
        int common = 0;
        while (common < b.Length && a[common] == b[common]) common++;
        return a.AsSpan(common + 1).SequenceEqual(b.AsSpan(common));
    }

    private static Regex Rx(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(120));
    private static HashSet<string> Set(string entries) => new(entries.Split(' '), StringComparer.Ordinal);
    private static Dictionary<string, string> Map(string entries)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries.Split(' '))
        {
            var fields = entry.Split('=');
            result[fields[0]] = fields[1];
        }
        return result;
    }
}

using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace HhScout.Api;

public static class VacancyDetailsParser
{
    public static VacancyDetails Parse(string html, string id, DateTimeOffset fetchedAt)
    {
        var document = new HtmlParser().ParseDocument(html);
        var description = document.QuerySelector("[data-qa='vacancy-description']");
        if (description is null) throw new HhException("Полное описание не найдено: вакансия закрыта или hh.ru изменил разметку. Предыдущие данные сохранены.");
        var text = new StringBuilder();
        Append(description, text);
        var readable = Regex.Replace(text.ToString(), @"[\t\u00a0 ]+", " ");
        readable = Regex.Replace(readable, @" *\n *", "\n");
        readable = Regex.Replace(readable, @"\n{3,}", "\n\n").Trim();
        if (readable.Length is < 10 or > 200_000) throw new HhException("Описание вакансии пустое или превышает допустимый размер.");
        return new(id, readable, fetchedAt);
    }

    private static void Append(INode node, StringBuilder text)
    {
        if (node is IText value) { text.Append(value.Data); return; }
        if (node is IElement element)
        {
            if (element.LocalName is "script" or "style" or "noscript") return;
            var block = element.LocalName is "p" or "div" or "li" or "ul" or "ol" or "br" or "h2" or "h3" or "h4";
            if (block) text.Append('\n');
            foreach (var child in node.ChildNodes) Append(child, text);
            if (block) text.Append('\n');
            return;
        }
        foreach (var child in node.ChildNodes) Append(child, text);
    }
}

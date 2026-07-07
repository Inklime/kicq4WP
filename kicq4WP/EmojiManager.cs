using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media.Imaging;

namespace kicq4WP
{
    // Класс для хранения данных одного смайлика (для пикера)
    public class EmojiItem
    {
        public string Code { get; set; }
        public string ImagePath { get; set; }
    }

    // Класс, который превращает текст в картинки
    public static class EmojiManager
    {
        public static readonly Dictionary<string, string> EmojiDict = new Dictionary<string, string>
        {
            { "O:-)", "ms-appx:///Assets/emoji/aa.gif" },
            { ":-)", "ms-appx:///Assets/emoji/ab.gif" },
            { ":-(", "ms-appx:///Assets/emoji/ac.gif" },
            { ";-)", "ms-appx:///Assets/emoji/ad.gif" },
            { ":-P", "ms-appx:///Assets/emoji/ae.gif" },
            { "8)", "ms-appx:///Assets/emoji/af.gif" },
            { ":-D", "ms-appx:///Assets/emoji/ag.gif" },
            { ":-[", "ms-appx:///Assets/emoji/ah.gif" },
            { "=-O", "ms-appx:///Assets/emoji/ai.gif" },
            { ":-*", "ms-appx:///Assets/emoji/aj.gif" },
            { ":'(", "ms-appx:///Assets/emoji/ak.gif" },
            { ":-X", "ms-appx:///Assets/emoji/al.gif" },
            { ">:o", "ms-appx:///Assets/emoji/am.gif" },
            { ":-|", "ms-appx:///Assets/emoji/an.gif" },
            { ":-\\", "ms-appx:///Assets/emoji/ao.gif" },
            { "*JOKINGLY*", "ms-appx:///Assets/emoji/ap.gif" },
            { "]:->", "ms-appx:///Assets/emoji/aq.gif" },
            { "[:-}", "ms-appx:///Assets/emoji/ar.gif" },
            { "*KISSED*", "ms-appx:///Assets/emoji/as.gif" },
            { ":-!", "ms-appx:///Assets/emoji/at.gif" },
            { "*TIRED*", "ms-appx:///Assets/emoji/au.gif" },
            { "*STOP*", "ms-appx:///Assets/emoji/av.gif" },
            { "*KISSING*", "ms-appx:///Assets/emoji/aw.gif" },
            { "@}->--", "ms-appx:///Assets/emoji/ax.gif" },
            { "*THUMBS UP*", "ms-appx:///Assets/emoji/ay.gif" },
            { "*DRINK*", "ms-appx:///Assets/emoji/az.gif" },
            { "*IN LOVE*", "ms-appx:///Assets/emoji/ba.gif" },
            { "@=", "ms-appx:///Assets/emoji/bb.gif" },
            { "*HELP*", "ms-appx:///Assets/emoji/bc.gif" },
            { "\\m/", "ms-appx:///Assets/emoji/bd.gif" },
            { "%)", "ms-appx:///Assets/emoji/be.gif" },
            { "*OK*", "ms-appx:///Assets/emoji/bf.gif" },
            { "*WASSUP*", "ms-appx:///Assets/emoji/bg.gif" },
            { "*SORRY*", "ms-appx:///Assets/emoji/bh.gif" },
            { "*BRAVO*", "ms-appx:///Assets/emoji/bi.gif" },
            { "*ROFL*", "ms-appx:///Assets/emoji/bj.gif" },
            { "*PARDON*", "ms-appx:///Assets/emoji/bk.gif" },
            { "*NO*", "ms-appx:///Assets/emoji/bl.gif" },
            { "*CRAZY*", "ms-appx:///Assets/emoji/bm.gif" },
            { "*DONT_KNOW*", "ms-appx:///Assets/emoji/bn.gif" },
            { "*DANCE*", "ms-appx:///Assets/emoji/bo.gif" },
            { "*YAHOO*", "ms-appx:///Assets/emoji/bp.gif" }

        };

        public static readonly DependencyProperty FormattedTextProperty =
            DependencyProperty.RegisterAttached("FormattedText", typeof(string), typeof(EmojiManager), new PropertyMetadata(null, OnTextChanged));

        public static void SetFormattedText(DependencyObject obj, string value) => obj.SetValue(FormattedTextProperty, value);
        public static string GetFormattedText(DependencyObject obj) => (string)obj.GetValue(FormattedTextProperty);

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            RichTextBlock richText = d as RichTextBlock;

            if (richText != null)
            {
                richText.Blocks.Clear();
                string text = e.NewValue as string ?? "";
                var paragraph = new Paragraph();

                int currentIndex = 0;
                while (currentIndex < text.Length)
                {
                    int firstMatchIndex = -1;
                    string matchedEmoji = null;

                    foreach (var emoji in EmojiDict.Keys)
                    {
                        int index = text.IndexOf(emoji, currentIndex);
                        if (index != -1 && (firstMatchIndex == -1 || index < firstMatchIndex))
                        {
                            firstMatchIndex = index;
                            matchedEmoji = emoji;
                        }
                    }

                    if (firstMatchIndex != -1)
                    {
                        if (firstMatchIndex > currentIndex)
                            paragraph.Inlines.Add(new Run { Text = text.Substring(currentIndex, firstMatchIndex - currentIndex) });

                        var img = new Image
                        {
                            Source = new BitmapImage(new Uri(EmojiDict[matchedEmoji])),
                            Width = 24,
                            Height = 24,
                            Margin = new Thickness(2, 0, 2, -4)
                        };
                        paragraph.Inlines.Add(new InlineUIContainer { Child = img });
                        currentIndex = firstMatchIndex + matchedEmoji.Length;
                    }
                    else
                    {
                        paragraph.Inlines.Add(new Run { Text = text.Substring(currentIndex) });
                        break;
                    }
                }
                richText.Blocks.Add(paragraph);
            }
        }
    }
}
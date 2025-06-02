namespace kicq4WP
{
    internal class Message
    {
        public string MessageText { get; set; }  // Переименовал поле в MessageText
        public string Sender { get; set; }       // Имя отправителя
        public string Time { get; set; }         // Время отправки

        public Message(string messageText, string sender, string time)
        {
            MessageText = messageText;
            Sender = sender;
            Time = time;
        }
    }
}

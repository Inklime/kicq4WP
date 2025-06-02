public class Contact
{
    public string Uin { get; set; }
    public string Name { get; set; }
    public string Group { get; set; }

    public override string ToString()
    {
        return string.IsNullOrEmpty(Group)
            ? $"{Name} ({Uin})"
            : $"{Name} ({Uin}) [{Group}]";
    }
}
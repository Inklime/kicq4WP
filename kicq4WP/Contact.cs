using System.Runtime.Serialization;

[DataContract]
public class Contact
{
    [DataMember]
    public string Uin { get; set; }

    [DataMember]
    public string Name { get; set; }

    [DataMember]
    public string Group { get; set; }

    [DataMember]
    public string StatusIcon { get; set; }

    [DataMember]
    public bool IsNewOnline { get; set; }


public override string ToString()
    {
        return string.IsNullOrEmpty(Group)
            ? $"{Name} ({Uin})"
            : $"{Name} ({Uin}) [{Group}]";
    }
}
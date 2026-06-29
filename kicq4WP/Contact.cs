using System;
using System.ComponentModel;
using System.Runtime.Serialization;

[DataContract]
public class Contact : INotifyPropertyChanged
{
    [DataMember]
    public string Uin { get; set; }

    [DataMember]
    public string Name { get; set; }

    [DataMember]
    public string Group { get; set; }

    [DataMember]
    public string XtrazIcon { get; set; }

    private string _statusIcon;

    [DataMember]
    public string StatusIcon
    {
        get { return _statusIcon; }
        set
        {
            _statusIcon = value;
            OnPropertyChanged("StatusIcon");
        }
    }

    private bool _isNewOnline;

    [DataMember]
    public bool IsNewOnline
    {
        get { return _isNewOnline; }
        set
        {
            _isNewOnline = value;
            OnPropertyChanged("IsNewOnline");
        }
    }

    private int _unreadCount;

    [DataMember]
    public int UnreadCount
    {
        get { return _unreadCount; }
        set
        {
            _unreadCount = value;
            OnPropertyChanged("UnreadCount");
            OnPropertyChanged("HasUnread");
        }
    }

    public bool HasUnread
    {
        get { return _unreadCount > 0; }
    }

    public override string ToString()
    {
        return string.IsNullOrEmpty(Group)
            ? string.Format("{0} ({1})", Name, Uin)
            : string.Format("{0} ({1}) [{2}]", Name, Uin, Group);
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        var handler = PropertyChanged;
        if (handler != null)
            handler(this, new PropertyChangedEventArgs(propertyName));
    }
}
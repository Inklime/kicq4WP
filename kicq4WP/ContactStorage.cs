using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using Windows.Storage;
using kicq4WP;

namespace kicq4WP
{
    public static class ContactStorage
    {
        public static async Task SaveContactsToFileAsync(string uin, ObservableCollection<Contact> contacts)
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(List<Contact>));

                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    $"contacts_{uin}.json", CreationCollisionOption.ReplaceExisting);

                using (var stream = await file.OpenStreamForWriteAsync())
                {
                    serializer.WriteObject(stream, contacts);
                }

                Debug.WriteLine($"[SaveContactsToFile] Saved contacts for UIN {uin}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SaveContactsToFile ERROR] " + ex.Message);
            }

        }

        public static async Task<List<Contact>> LoadContactsFromFileAsync(string uin)
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync($"contacts_{uin}.json");

                using (var stream = await file.OpenStreamForReadAsync())
                {
                    var serializer = new DataContractJsonSerializer(typeof(List<Contact>));
                    var contacts = (List<Contact>)serializer.ReadObject(stream);
                    return contacts;
                }
            }
            catch (FileNotFoundException)
            {
                Debug.WriteLine($"[LoadContactsFromFile] No saved contacts for UIN {uin}");
                return new List<Contact>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LoadContactsFromFile ERROR] " + ex.Message);
                return new List<Contact>();
            }
        }

    }
}

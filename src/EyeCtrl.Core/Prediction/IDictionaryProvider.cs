namespace EyeCtrl.Core.Prediction;

public interface IDictionaryProvider
{
    Dictionary<string, int> Load();
    void Save(Dictionary<string, int> words);
}

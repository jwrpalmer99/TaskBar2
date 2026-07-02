namespace TaskBar2.Services;

internal interface ISecondaryTaskbarHost
{
    void Show();

    void Close();

    void RefreshPlacement();
}

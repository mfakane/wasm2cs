using System;
using UnityEngine;

public static class UnitySelfHostEntry
{
    public static void Run()
    {
        try
        {
            UnitySelfHostRunner.Run();
        }
        catch (Exception exception)
        {
            UnitySelfHostAdapter.Log(exception.ToString());
            if (!Application.isEditor) Application.Quit(1);
            throw;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void PlayerRun()
    {
        if (Application.isEditor) return;
        try
        {
            UnitySelfHostRunner.Run();
        }
        catch (Exception exception)
        {
            UnitySelfHostAdapter.Log(exception.ToString());
            Application.Quit(1);
        }
    }
}

using UnityEngine;

// One line of dialogue. RevealsName: the character says her name here, so the name header changes from "???".
public struct Beat
{
    public string Text;
    public bool RevealsName;
}

// The story beats, in order. The index is the beat's number, used by the plot scene and by the builder and gameplay triggers.
public static class Story
{
    public static readonly Beat[] Beats =
    {
        new Beat { Text = "You’re five minutes late. Wasted time burns money and I don’t tolerate dead weight." },
        new Beat { Text = "No prior launch experience, I hear." },
        new Beat { Text = "I’m Doctor Ashford. I’m supervising you today. Keep up.", RevealsName = true },
        new Beat { Text = "Come on in to the Vertical Assembly Building." },
        new Beat { Text = "It is not brain surgery. Hover on every part of the rocket and see its stats, they are your main helpers when you choose between all of them." },
        new Beat { Text = "Calculate what can give you the best result. It is easier in practice, so start now." },
        new Beat { Text = "You are already finished? Good. Next step might confuse you a little bit more, but I am here to assure you are prepared. Lucky you." },
        new Beat { Text = "Click “!” to find out what each button means." },
        new Beat { Text = "Good luck and remember about the consequences of your choice." },
    };

    // The plot scene plays the first five beats. The rest are triggered where they belong.
    public static readonly int[] Intro = { 0, 1, 2, 3, 4 };
    public const int Calculate = 5;    // the rocket is built and can launch
    public const int Finished = 6;     // the player launches from the builder for the first time
    public const int ClickInfo = 7;    // first arrival in the gameplay scene
    public const int GoodLuck = 8;     // first arrival in the gameplay scene

    public const string Name = "Doctor Ashford";
    public const string Unknown = "???";

    // Seen flags are per viewer, kept in PlayerPrefs, so each story moment plays once.
    public static bool Seen(string key) => PlayerPrefs.GetInt("story." + key, 0) == 1;

    public static void MarkSeen(string key)
    {
        PlayerPrefs.SetInt("story." + key, 1);
        PlayerPrefs.Save();
    }

    public static bool NameKnown => PlayerPrefs.GetInt("story.name", 0) == 1;

    public static void RevealName()
    {
        PlayerPrefs.SetInt("story.name", 1);
        PlayerPrefs.Save();
    }
}

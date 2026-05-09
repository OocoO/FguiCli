using UnityEditor;
using UnityEngine;

/// <summary>
/// Minimal single-line input dialog for editor scripts.
/// </summary>
public class EditorInputDialog : EditorWindow
{
    private string _value = string.Empty;
    private bool _confirmed;
    private bool _done;
    private string _label = string.Empty;

    public static string Show(string title, string label, string defaultValue = "")
    {
        EditorInputDialog window = CreateInstance<EditorInputDialog>();
        window.titleContent = new GUIContent(title);
        window._label = label;
        window._value = defaultValue ?? string.Empty;
        window._confirmed = false;
        window._done = false;
        window.minSize = new Vector2(340, 90);
        window.maxSize = new Vector2(340, 90);
        window.ShowModalUtility();
        return window._confirmed ? window._value : null;
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField(_label);
        GUI.SetNextControlName("InputField");
        _value = EditorGUILayout.TextField(_value);
        EditorGUI.FocusTextInControl("InputField");

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("OK", GUILayout.Width(80)))
        {
            _confirmed = true;
            _done = true;
            Close();
        }
        if (GUILayout.Button("Cancel", GUILayout.Width(80)))
        {
            _confirmed = false;
            _done = true;
            Close();
        }
        EditorGUILayout.EndHorizontal();

        if (!_done && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return)
        {
            _confirmed = true;
            _done = true;
            Close();
        }
    }
}


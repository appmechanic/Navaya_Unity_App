using System.Collections;
using UnityEngine;
using TMPro;
using System;

public class SessionOverlayUI : MonoBehaviour
{
    public CanvasGroup overlayGroup;
    public TMP_Text titleText;
    public TMP_Text subtitleText;

    public float fadeDuration = 0.5f;

    private Coroutine currentRoutine;
    public Action OnFadeOutComplete;

    private void Awake()
    {
        HideInstant();
    }

    public void ShowOverlay(string command)
    {
        if (currentRoutine != null) StopCoroutine(currentRoutine);

        if (command == "startSession")
        {
            titleText.text = "Preparing Your Experience";
            subtitleText.text = "Please relax while your session is being prepared.";
            currentRoutine = StartCoroutine(ShowThenHideRoutine(4f));
        }
        else if (command == "endSession")
        {
            titleText.text = "Session Ended";
            subtitleText.text = "Please wait for the next session to begin.";
            currentRoutine = StartCoroutine(FadeIn());
        }
    }

    public void HideOverlay()
    {
        if (currentRoutine != null) StopCoroutine(currentRoutine);
        currentRoutine = StartCoroutine(FadeOut());
    }

    private IEnumerator ShowThenHideRoutine(float delay)
    {
        yield return FadeIn();
        yield return new WaitForSeconds(delay);
        yield return FadeOut();
    }

    private IEnumerator FadeIn()
    {
        overlayGroup.alpha = 0;

        float t = 0;
        while (t < fadeDuration)
        {
            overlayGroup.alpha = Mathf.Lerp(0, 1, t / fadeDuration);
            t += Time.deltaTime;
            yield return null;
        }
        overlayGroup.alpha = 1;
    }

    private IEnumerator FadeOut()
    {
        float t = 0;
        while (t < fadeDuration)
        {
            overlayGroup.alpha = Mathf.Lerp(1, 0, t / fadeDuration);
            t += Time.deltaTime;
            yield return null;
        }
        overlayGroup.alpha = 0;
        OnFadeOutComplete?.Invoke();
    }

    public void HideInstant()
    {
        overlayGroup.alpha = 0;
    }
}
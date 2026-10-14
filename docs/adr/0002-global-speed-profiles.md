# Speed profiles are global, never per camera

Sensitivity is changed during a show by stepping through one ordered list of speed profiles, each setting pan, tilt, zoom and focus together, and the active profile applies to every camera. We rejected a per-camera speed setting because cameras quietly ending up at different sensitivities mid-show is the kind of surprise that breaks a live operator; per-input "Max speed" stays for shaping each control. Companion steps the list and reads the active profile back, so the app still never calls Companion (#41).

try:
    import PIL
    print('PIL', PIL.__version__)
except Exception as exc:
    print('NO_PIL', repr(exc))

try:
    import cv2
    print('cv2', cv2.__version__)
except Exception as exc:
    print('NO_CV2', repr(exc))

try:
    import pytesseract
    print('pytesseract', pytesseract.__version__)
except Exception as exc:
    print('NO_PYTESSERACT', repr(exc))

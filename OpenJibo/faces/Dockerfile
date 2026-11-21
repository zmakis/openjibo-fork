# OpenJibo local face recogniser (OpenCV YuNet + SFace). Built by docker compose (service "faces").
FROM python:3.12-slim
RUN pip install --no-cache-dir opencv-python-headless==4.10.0.84 numpy==2.1.3
ADD https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx /models/face_detection_yunet.onnx
ADD https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx /models/face_recognition_sface.onnx
COPY faces_service.py teach.py /app/
WORKDIR /app
EXPOSE 8487
CMD ["python", "-u", "/app/faces_service.py"]

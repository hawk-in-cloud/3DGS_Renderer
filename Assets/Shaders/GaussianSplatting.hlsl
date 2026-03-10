#ifndef GAUSSIAN_SPLATTING_HLSL
#define GAUSSIAN_SPLATTING_HLSL

// 用四元数旋转向量 v。
float3 QuatRotateVector(float3 v, float4 r)
{
    float3 t = 2 * cross(r.xyz, v);
    return v + r.w * t + cross(r.xyz, t);
}

// 四元数逆。
float4 QuatInverse(float4 q)
{
    return rcp(dot(q, q)) * q * float4(-1,-1,-1,1);
}

// Sigmoid: 把任意实数映射到 (0,1)。
float Sigmoid(float v)
{
    return rcp(1.0 + exp(-v));
}

// 由旋转(四元数)和缩放构建 3x3 矩阵 M=R*S。
float3x3 CalcMatrixFromRotationScale(float4 rot, float3 scale)
{
    float3x3 ms = float3x3(
        scale.x, 0, 0,
        0, scale.y, 0,
        0, 0, scale.z
    );

    float x = rot.x;
    float y = rot.y;
    float z = rot.z;
    float w = rot.w;

    float3x3 mr = float3x3(
        1 - 2 * (y * y + z * z),     2 * (x * y - w * z),     2 * (x * z + w * y),
            2 * (x * y + w * z), 1 - 2 * (x * x + z * z),     2 * (y * z - w * x),
            2 * (x * z - w * y),     2 * (y * z + w * x), 1 - 2 * (x * x + y * y)
    );

    return mul(mr, ms);
}

// 计算 3D 协方差 Σ3D = M * M^T。
// 为了节省插值寄存器，这里用两个 float3 拆分存储 6 个独立元素。
void CalcCovariance3D(float3x3 rotMat, out float3 sigma0, out float3 sigma1)
{
    float3x3 sig = mul(rotMat, transpose(rotMat));
    sigma0 = float3(sig._m00, sig._m01, sig._m02);
    sigma1 = float3(sig._m11, sig._m12, sig._m22);
}

// 依据 EWA Splatting (Zwicker et al. 2002, eq.31)
// 把 3D 协方差投影到屏幕平面的 2D 协方差。
float3 CalcCovariance2D(float3 worldPos, float3 cov3d0, float3 cov3d1)
{
    float4x4 viewMatrix = UNITY_MATRIX_V;
    float3 viewPos = mul(viewMatrix, float4(worldPos, 1)).xyz;

    // 处理强裁剪边缘情况，避免协方差数值过度发散。
    float aspect = UNITY_MATRIX_P._m00 / UNITY_MATRIX_P._m11;
    float tanFovX = rcp(UNITY_MATRIX_P._m00);
    float tanFovY = rcp(UNITY_MATRIX_P._m11 * aspect);
    float limX = 1.3 * tanFovX;
    float limY = 1.3 * tanFovY;
    viewPos.x = clamp(viewPos.x / viewPos.z, -limX, limX) * viewPos.z;
    viewPos.y = clamp(viewPos.y / viewPos.z, -limY, limY) * viewPos.z;

    // 焦距（像素单位），用于 Jacobian。
    float focal = _ScreenParams.x * UNITY_MATRIX_P._m00 / 2;

    // 透视投影关于 (x,y,z) 的 Jacobian（线性近似）。
    float4x4 J = float4x4(
        focal / viewPos.z, 0, -(focal * viewPos.x) / (viewPos.z * viewPos.z), 0,
        0, focal / viewPos.z, -(focal * viewPos.y) / (viewPos.z * viewPos.z), 0,
        0, 0, 0, 0,
        0, 0, 0, 0
    );

    // 只保留旋转部分参与协方差变换，不要平移项。
    viewMatrix._m03_m13_m23 = 0;
    float4x4 W = viewMatrix;

    // T = J * W，最终 Σ2D = T * Σ3D * T^T。
    float4x4 T = mul(J, W);

    float4x4 V = float4x4(
        cov3d0.x, cov3d0.y, cov3d0.z, 0,
        cov3d0.y, cov3d1.x, cov3d1.y, 0,
        cov3d0.z, cov3d1.y, cov3d1.z, 0,
        0, 0, 0, 0
    );

    float4x4 cov = mul(T, mul(V, transpose(T)));

    // 最小核大小约束（低通），防止半径过小导致闪烁。
    cov._m00 += 0.3;
    cov._m11 += 0.3;

    // 返回 2x2 对称矩阵 [xx, xy, yy]。
    return float3(cov._m00, cov._m01, cov._m11);
}

// conic 是 2D 协方差矩阵的逆：
// inv([a b; b c]) = 1/(ac-b^2) * [c -b; -b a]
// 这里 cov2d = [a,b,c]，返回 conic=[c/det, -b/det, a/det]。
float3 CalcConic(float3 cov2d)
{
    float det = cov2d.x * cov2d.z - cov2d.y * cov2d.y;
    return float3(cov2d.z, -cov2d.y, cov2d.x) * rcp(det);
}

// 计算片元与高斯中心在屏幕像素空间的偏移，修正 Y 翻转约定。
float2 CalcScreenSpaceDelta(float2 svPositionXY, float2 centerXY)
{
    float2 d = svPositionXY - centerXY;
    d.y *= _ProjectionParams.x;
    return d;
}

// 计算二维高斯指数项：-1/2 * d^T * C * d。
float CalcPowerFromConic(float3 conic, float2 d)
{
    return -0.5 * (conic.x * d.x * d.x + conic.z * d.y * d.y) + conic.y * d.x * d.y;
}

#endif // GAUSSIAN_SPLATTING_HLSL

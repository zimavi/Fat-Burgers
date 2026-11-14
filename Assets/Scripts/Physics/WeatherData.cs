using System;
using UnityEngine;
/*When calculating the actual flight, air density must be obtained via `CalculateDensity()` 
 * based on the actual temperature and pressure at the rocket's current altitude.

For the prediction, use the density value entered by the player (`density`), with 
interpolation between the measured layers. Do not recalculate or automatically correct 
it—any error on the player's part should affect the prediction.*/
[CreateAssetMenu(
    menuName = "Fat Burgers/Weather Data",
    fileName = "WeatherData")]
public class WeatherData : ScriptableObject
{
    public WeatherLayer[] layers;

    [Serializable]
    public struct WeatherLayer
    {
        public float CalculateDensity()
        {
            return pressurePa / (287.05f * (temperatureC + 273.15f));
        }
        [Min(-150)]public float altitude;     // м
        [Min(-273)] public float temperatureC; // °C
        [Min(0)] public float pressurePa;   // Па
        public float windSpeed;    // м/s: + right - left
        public float density; // кг/м³, user inputs 
    }
}